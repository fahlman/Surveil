//! The building configuration: a list of buildings, each a name and a set of
//! named ranges ("First Floor" → `192.168.10.0/24`). One shape spans a small
//! site with one range to a campus with many buildings and named ranges.
//! `buildings.json` holds all the network parameters; this module is
//! the general parser and scan engine.

use std::collections::HashSet;
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

/// Prevent an accidental broad CIDR from allocating or probing millions of
/// addresses. A /16 (65,534 hosts) remains valid for large installations.
pub const MAX_SCAN_ADDRESSES: usize = 65_534;

impl Config {
    pub fn validate(&self) -> Result<(), String> {
        let mut building_names = HashSet::new();
        let mut ranges: Vec<(String, ipnet::Ipv4Net)> = Vec::new();

        for building in &self.buildings {
            let name = building.name.trim();
            if name.is_empty() {
                return Err("every building needs a name".into());
            }
            if !building_names.insert(name.to_ascii_lowercase()) {
                return Err(format!("duplicate building name: {name}"));
            }

            let mut area_names = HashSet::new();
            for range in &building.ranges {
                let area = range.name.trim();
                if area.is_empty() {
                    return Err(format!("every range in {name} needs a name"));
                }
                if !area_names.insert(area.to_ascii_lowercase()) {
                    return Err(format!("duplicate range name in {name}: {area}"));
                }
                let spec = range.cidr.trim();
                let net: ipnet::Ipv4Net = if spec.contains('/') {
                    spec.parse()
                        .map_err(|e| format!("invalid range '{}' in {name}: {e}", range.cidr))?
                } else {
                    let ip: Ipv4Addr = spec
                        .parse()
                        .map_err(|e| format!("invalid address '{}' in {name}: {e}", range.cidr))?;
                    ipnet::Ipv4Net::new(ip, 32).map_err(|e| e.to_string())?
                };
                if !net.network().is_private() || !net.broadcast().is_private() {
                    return Err(format!(
                        "range '{}' in {name} is not entirely private",
                        range.cidr
                    ));
                }
                if let Some((other, _)) = ranges.iter().find(|(_, existing)| {
                    existing.contains(&net.network()) || net.contains(&existing.network())
                }) {
                    return Err(format!("range '{}' in {name} overlaps {other}", range.cidr));
                }
                ranges.push((format!("{} in {name}", range.cidr), net));
            }
        }
        Ok(())
    }
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
        if net.hosts().size_hint().0 > MAX_SCAN_ADDRESSES {
            return Err(format!(
                "scan exceeds the safety limit of {MAX_SCAN_ADDRESSES} addresses; select fewer or smaller ranges"
            ));
        }
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
    let mut out = HashSet::new();
    for spec in specs {
        for ip in range_hosts(spec)? {
            if ip.is_private() {
                out.insert(ip);
                if out.len() > MAX_SCAN_ADDRESSES {
                    return Err(format!(
                        "scan exceeds the safety limit of {MAX_SCAN_ADDRESSES} addresses; select fewer or smaller ranges"
                    ));
                }
            }
        }
    }
    if out.is_empty() {
        return Err("no scan targets selected".into());
    }
    let mut private: Vec<Ipv4Addr> = out.into_iter().collect();
    if private.is_empty() {
        return Err(
            "selected targets resolve to public addresses — Surveil only scans private ranges (10/8, 172.16/12, 192.168/16)"
                .into(),
        );
    }
    private.sort_by_key(|ip| u32::from(*ip));
    Ok(private)
}

impl Config {
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
        let config = cfg(vec![
            (
                "Example Hall",
                vec![
                    ("First Floor", "10.200.61.0/30"),
                    ("Second Floor", "10.200.62.0/30"),
                ],
            ),
            ("Example Annex", vec![("Main", "10.200.63.0/30")]),
        ]);
        let cidrs = config
            .buildings
            .iter()
            .flat_map(|b| b.ranges.iter().map(|r| r.cidr.clone()))
            .collect::<Vec<_>>();
        let addrs = expand_private(&cidrs).unwrap();
        assert_eq!(addrs.len(), 6); // three /30s × 2 usable
        assert!(addrs.iter().all(|ip| ip.is_private()));
    }

    #[test]
    fn refuses_public_ranges() {
        assert!(expand_private(&["8.8.8.0/30".to_string()]).is_err());
    }

    #[test]
    fn validates_duplicate_and_overlapping_ranges() {
        let duplicate = cfg(vec![(
            "Example",
            vec![("One", "192.168.1.0/24"), ("Two", "192.168.1.0/25")],
        )]);
        assert!(duplicate.validate().unwrap_err().contains("overlaps"));
    }

    #[test]
    fn limits_accidental_huge_scans() {
        let error = expand_private(&["10.0.0.0/8".to_string()]).unwrap_err();
        assert!(error.contains("safety limit"));
    }

    #[test]
    fn locates_building_and_range() {
        let c = cfg(vec![(
            "Example Hall",
            vec![
                ("First Floor", "10.200.61.0/24"),
                ("Second Floor", "10.200.62.0/24"),
            ],
        )]);
        assert_eq!(
            c.locate(Ipv4Addr::new(10, 200, 62, 137)),
            Some(("Example Hall", "Second Floor"))
        );
        assert!(c.locate(Ipv4Addr::new(10, 99, 0, 1)).is_none());
    }
}
