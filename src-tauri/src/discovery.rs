//! Concurrent unicast host discovery.
//!
//! WS-Discovery is multicast and won't cross VLANs, so we sweep addresses
//! directly with bounded-concurrency TCP-connect probes, reporting progress as
//! we go. Concurrency is bounded with `buffer_unordered` rather than one task
//! per address, so a large sweep holds a fixed working set.

use std::net::{IpAddr, Ipv4Addr, SocketAddr};
use std::time::Duration;

use futures::stream::StreamExt;
use tokio::net::TcpStream;
use tokio::time::timeout;

// Report progress at least this often, so a long sweep keeps the UI moving even
// across stretches that find nothing.
const PROGRESS_EVERY: usize = 256;

/// One TCP-connect probe: true if the host answers on this port before timeout.
pub async fn probe(addr: SocketAddr, timeout_ms: u64) -> bool {
    matches!(
        timeout(Duration::from_millis(timeout_ms), TcpStream::connect(addr)).await,
        Ok(Ok(_))
    )
}

/// Concurrently probe every address. `on_progress(scanned, total, found)` is
/// called periodically (and whenever a host is found) so the UI can show a live
/// bar. Returns the reachable addresses, sorted numerically.
pub async fn sweep(
    addresses: Vec<Ipv4Addr>,
    port: u16,
    concurrency: usize,
    timeout_ms: u64,
    mut on_progress: impl FnMut(usize, usize, usize),
) -> Vec<Ipv4Addr> {
    let total = addresses.len();
    let concurrency = concurrency.max(1);

    let mut found: Vec<Ipv4Addr> = Vec::new();
    let mut scanned = 0usize;

    let mut probes = futures::stream::iter(addresses.into_iter().map(|ip| async move {
        let hit = probe(SocketAddr::new(IpAddr::V4(ip), port), timeout_ms).await;
        (ip, hit)
    }))
    .buffer_unordered(concurrency);

    while let Some((ip, hit)) = probes.next().await {
        scanned += 1;
        if hit {
            found.push(ip);
        }
        if hit || scanned % PROGRESS_EVERY == 0 || scanned == total {
            on_progress(scanned, total, found.len());
        }
    }

    found.sort_by_key(|ip| u32::from(*ip));
    found
}

#[cfg(test)]
mod tests {
    use super::*;
    use tokio::net::TcpListener;

    fn block_on<F: std::future::Future>(fut: F) -> F::Output {
        tokio::runtime::Runtime::new().unwrap().block_on(fut)
    }

    #[test]
    fn sweep_finds_listener_and_reports_progress() {
        block_on(async {
            let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
            let port = listener.local_addr().unwrap().port();

            // One address listens, one does not.
            let addrs = vec![Ipv4Addr::new(127, 0, 0, 1), Ipv4Addr::new(127, 0, 0, 2)];
            let mut progress_calls = 0usize;
            let mut last_found = 0usize;

            let found = sweep(addrs, port, 8, 300, |_scanned, _total, found| {
                progress_calls += 1;
                last_found = found;
            })
            .await;

            assert_eq!(found, vec![Ipv4Addr::new(127, 0, 0, 1)]);
            assert!(progress_calls >= 1, "progress should be reported");
            assert_eq!(last_found, 1, "the one live host should be counted");
        });
    }
}
