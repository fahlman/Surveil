//! The building configuration: a list of buildings, each a name and a set of
//! named ranges ("First Floor" → `192.168.10.0/24`). One shape spans a small
//! site with one range to a campus with many buildings and named ranges.
//! `buildings.json` holds all the network parameters; this module is
//! the general parser and scan engine.

use std::net::Ipv4Addr;

use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct Config {
    #[serde(default)]
    pub buildings: Vec<Building>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Building {
    pub name: String,
    #[serde(default)]
    pub ranges: Vec<Range>,
    #[serde(default)]
    pub notes: String,
}

/// A named network range — an "extension" of a building (e.g. a floor).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Range {
    #[serde(default)]
    pub name: String,
    pub cidr: String,
}

fn range_hosts(spec: &str) -> Result<Vec<Ipv4Addr>, String> {
    let spec = spec.trim();
    if spec.is_empty() {
        return Ok(Vec::new());
    }
    if spec.contains('/') {
        let net: ipnet::Ipv4Net = spec
            .parse()
            .map_err(|e| format!("invalid range '{spec}': {e}"))?;
        Ok(net.hosts().collect())
    } else {
        let ip: Ipv4Addr = spec
            .parse()
            .map_err(|e| format!("invalid address '{spec}': {e}"))?;
        Ok(vec![ip])
    }
}

fn range_contains(spec: &str, ip: Ipv4Addr) -> bool {
    let spec = spec.trim();
    if spec.contains('/') {
        spec.parse::<ipnet::Ipv4Net>()
            .map(|net| net.contains(&ip))
            .unwrap_or(false)
    } else {
        spec.parse::<Ipv4Addr>().map(|a| a == ip).unwrap_or(false)
    }
}

/// Expand a chosen set of CIDRs/addresses to their host addresses, filtered to
/// RFC 1918 private space. This is the single chokepoint every scan flows
/// through — public targets can never be probed, whatever is selected.
pub fn expand_private(specs: &[String]) -> Result<Vec<Ipv4Addr>, String> {
    let mut out = Vec::new();
    for spec in specs {
        out.extend(range_hosts(spec)?);
    }
    if out.is_empty() {
        return Err("no scan targets selected".into());
    }
    let private: Vec<Ipv4Addr> = out.into_iter().filter(|ip| ip.is_private()).collect();
    if private.is_empty() {
        return Err(
            "selected targets resolve to public addresses — Surveil only scans private ranges (10/8, 172.16/12, 192.168/16)"
                .into(),
        );
    }
    Ok(private)
}

impl Config {
    /// Every address to probe: the union of all ranges, filtered to RFC 1918
    /// private space so a stray public range can never be scanned.
    pub fn scan_addresses(&self) -> Result<Vec<Ipv4Addr>, String> {
        let cidrs: Vec<String> = self
            .buildings
            .iter()
            .flat_map(|b| b.ranges.iter().map(|r| r.cidr.clone()))
            .collect();
        expand_private(&cidrs)
    }

    /// The (building name, range name) whose range contains this address.
    pub fn locate(&self, ip: Ipv4Addr) -> Option<(&str, &str)> {
        for building in &self.buildings {
            for range in &building.ranges {
                if range_contains(&range.cidr, ip) {
                    return Some((building.name.as_str(), range.name.as_str()));
                }
            }
        }
        None
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn cfg(buildings: Vec<(&str, Vec<(&str, &str)>)>) -> Config {
        Config {
            buildings: buildings
                .into_iter()
                .map(|(name, ranges)| Building {
                    name: name.to_string(),
                    ranges: ranges
                        .into_iter()
                        .map(|(rn, cidr)| Range {
                            name: rn.to_string(),
                            cidr: cidr.to_string(),
                        })
                        .collect(),
                    notes: String::new(),
                })
                .collect(),
        }
    }

    #[test]
    fn scans_the_union_of_private_ranges() {
        let addrs = cfg(vec![
            ("Example Hall", vec![("First Floor", "10.200.61.0/30"), ("Second Floor", "10.200.62.0/30")]),
            ("Example Annex", vec![("Main", "10.200.63.0/30")]),
        ])
        .scan_addresses()
        .unwrap();
        assert_eq!(addrs.len(), 6); // three /30s × 2 usable
        assert!(addrs.iter().all(|ip| ip.is_private()));
    }

    #[test]
    fn refuses_public_ranges() {
        assert!(cfg(vec![("Bad", vec![("x", "8.8.8.0/30")])])
            .scan_addresses()
            .is_err());
    }

    #[test]
    fn locates_building_and_range() {
        let c = cfg(vec![(
            "Example Hall",
            vec![("First Floor", "10.200.61.0/24"), ("Second Floor", "10.200.62.0/24")],
        )]);
        assert_eq!(
            c.locate(Ipv4Addr::new(10, 200, 62, 137)),
            Some(("Example Hall", "Second Floor"))
        );
        assert!(c.locate(Ipv4Addr::new(10, 99, 0, 1)).is_none());
    }
}
