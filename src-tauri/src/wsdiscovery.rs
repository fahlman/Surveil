//! ONVIF WS-Discovery probe — a diagnostic.
//!
//! Sends a single WS-Discovery Probe to the multicast group
//! `239.255.255.250:3702` and collects ProbeMatch replies. Because multicast
//! doesn't cross VLANs on its own, *who answers* tells us whether the network
//! forwards discovery: replies from only the local subnet → no forwarding;
//! replies from many buildings → a relay/gateway is carrying it fleet-wide.

use std::collections::HashMap;
use std::net::Ipv4Addr;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use tokio::net::UdpSocket;
use tokio::time::{timeout, Instant};

const WSD_ADDR: &str = "239.255.255.250:3702";

pub struct Responder {
    pub ip: Ipv4Addr,
    pub xaddrs: String,
}

/// Fire the probe and gather unique responders until `timeout_ms` elapses.
pub async fn probe(timeout_ms: u64) -> Result<Vec<Responder>, String> {
    let sock = UdpSocket::bind("0.0.0.0:0")
        .await
        .map_err(|e| format!("bind UDP socket: {e}"))?;
    // Best-effort: let the probe cross a few router hops if the network actually
    // multicast-routes it. A reflector/gateway doesn't need this.
    let _ = sock.set_multicast_ttl_v4(8);

    let message = build_probe();
    // UDP multicast is lossy; send a couple of times.
    for _ in 0..2 {
        sock.send_to(message.as_bytes(), WSD_ADDR)
            .await
            .map_err(|e| format!("send probe: {e}"))?;
    }

    let deadline = Instant::now() + Duration::from_millis(timeout_ms.max(500));
    let mut seen: HashMap<Ipv4Addr, Responder> = HashMap::new();
    let mut buf = vec![0u8; 65_535];

    loop {
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            break;
        }
        match timeout(remaining, sock.recv_from(&mut buf)).await {
            Ok(Ok((n, std::net::SocketAddr::V4(src)))) => {
                let body = String::from_utf8_lossy(&buf[..n]);
                if body.contains("ProbeMatch") || body.contains("XAddrs") {
                    let ip = *src.ip();
                    seen.entry(ip).or_insert_with(|| Responder {
                        ip,
                        xaddrs: extract(&body, "XAddrs>"),
                    });
                }
            }
            Ok(Ok(_)) => {} // ignore IPv6 sources
            Ok(Err(e)) => return Err(format!("recv: {e}")),
            Err(_) => break, // deadline reached
        }
    }

    let mut out: Vec<Responder> = seen.into_values().collect();
    out.sort_by_key(|r| u32::from(r.ip));
    Ok(out)
}

fn build_probe() -> String {
    format!(
        concat!(
            r#"<?xml version="1.0" encoding="UTF-8"?>"#,
            r#"<e:Envelope xmlns:e="http://www.w3.org/2003/05/soap-envelope""#,
            r#" xmlns:w="http://schemas.xmlsoap.org/ws/2004/08/addressing""#,
            r#" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery""#,
            r#" xmlns:dn="http://www.onvif.org/ver10/network/wsdl">"#,
            r#"<e:Header><w:MessageID>{}</w:MessageID>"#,
            r#"<w:To e:mustUnderstand="true">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>"#,
            r#"<w:Action e:mustUnderstand="true">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>"#,
            r#"</e:Header><e:Body><d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe></e:Body>"#,
            r#"</e:Envelope>"#,
        ),
        message_id()
    )
}

/// A per-probe message id in urn:uuid form, derived from the clock (no crate).
fn message_id() -> String {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos())
        .unwrap_or(0);
    let b = nanos.to_be_bytes(); // 16 bytes → uuid 8-4-4-4-12 layout
    format!(
        "urn:uuid:{:02x}{:02x}{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}-{:02x}{:02x}{:02x}{:02x}{:02x}{:02x}",
        b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]
    )
}

/// Pull the text right after an opening tag ending in `after_tag` up to the next
/// `<`. Prefix-agnostic (`<d:XAddrs>` and `<wsdd:XAddrs>` both match "XAddrs>").
fn extract(body: &str, after_tag: &str) -> String {
    if let Some(i) = body.find(after_tag) {
        let start = i + after_tag.len();
        if let Some(end) = body[start..].find('<') {
            return body[start..start + end].trim().to_string();
        }
    }
    String::new()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn probe_message_is_well_formed() {
        let p = build_probe();
        assert!(p.contains("http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe"));
        assert!(p.contains("dn:NetworkVideoTransmitter"));
        assert!(p.contains("urn:uuid:"));
    }

    #[test]
    fn message_id_has_uuid_shape() {
        let id = message_id();
        assert!(id.starts_with("urn:uuid:"));
        let uuid = &id["urn:uuid:".len()..];
        assert_eq!(uuid.len(), 36);
        assert_eq!(uuid.matches('-').count(), 4);
    }

    #[test]
    fn extract_pulls_tag_content() {
        let body = "<d:XAddrs>http://192.168.10.7/onvif/device_service</d:XAddrs>\
                    <d:Scopes>onvif://www.onvif.org/name/CamA</d:Scopes>";
        assert_eq!(
            extract(body, "XAddrs>"),
            "http://192.168.10.7/onvif/device_service"
        );
        assert_eq!(extract(body, "Scopes>"), "onvif://www.onvif.org/name/CamA");
    }
}
