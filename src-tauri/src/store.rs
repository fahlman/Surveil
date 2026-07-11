//! Persistence in the app-data folder, plus the scan-to-scan diff.
//!
//! `buildings.json` holds the `Config` (buildings with named ranges), seeded
//! from an embedded sample on first launch and migrated forward from every
//! older format. `cameras.json` holds the inventory — every camera ever seen,
//! with first/last-seen — which each scan diffs against.

use std::collections::{HashMap, HashSet};
use std::fs;
use std::net::Ipv4Addr;
use std::path::Path;

use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::config::{Building, Config, Range};

pub const CONFIG_FILE: &str = "buildings.json";
pub const INVENTORY_FILE: &str = "cameras.json";

pub fn load_config(dir: &Path, seed: &str) -> Result<Config, String> {
    let path = dir.join(CONFIG_FILE);

    if !path.exists() {
        fs::create_dir_all(dir).map_err(|e| format!("create data dir: {e}"))?;
        fs::write(&path, seed).map_err(|e| format!("seed {CONFIG_FILE}: {e}"))?;
        return serde_json::from_str(seed).map_err(|e| format!("parse seed: {e}"));
    }

    let text = fs::read_to_string(&path).map_err(|e| format!("read {CONFIG_FILE}: {e}"))?;
    let value: Value =
        serde_json::from_str(&text).map_err(|e| format!("parse {CONFIG_FILE}: {e}"))?;

    let (config, migrated) = if value.get("network").is_some() {
        let legacy: OctetConfig =
            serde_json::from_value(value).map_err(|e| format!("parse legacy config: {e}"))?;
        (migrate_octet(legacy), true)
    } else if value.is_array() {
        let legacy: Vec<OldBuilding> =
            serde_json::from_value(value).map_err(|e| format!("parse legacy buildings: {e}"))?;
        (migrate_array(legacy), true)
    } else if ranges_are_strings(&value) {
        let bare: BareConfig =
            serde_json::from_value(value).map_err(|e| format!("parse bare config: {e}"))?;
        (migrate_bare(bare), true)
    } else {
        (
            serde_json::from_value(value).map_err(|e| format!("parse {CONFIG_FILE}: {e}"))?,
            false,
        )
    };

    if migrated {
        save_config(dir, &config)?;
    }
    Ok(config)
}

pub fn save_config(dir: &Path, config: &Config) -> Result<(), String> {
    fs::create_dir_all(dir).map_err(|e| format!("create data dir: {e}"))?;
    let text = serde_json::to_string_pretty(config).map_err(|e| e.to_string())?;
    fs::write(dir.join(CONFIG_FILE), text).map_err(|e| format!("write {CONFIG_FILE}: {e}"))
}

/// Whether the config's ranges are bare CIDR strings (the previous format)
/// rather than `{name, cidr}` objects.
fn ranges_are_strings(value: &Value) -> bool {
    value
        .get("buildings")
        .and_then(Value::as_array)
        .and_then(|buildings| {
            buildings
                .iter()
                .find_map(|b| b.get("ranges").and_then(Value::as_array).and_then(|r| r.first()))
        })
        .map(Value::is_string)
        .unwrap_or(false)
}

// --- Naming floors from their 3rd octet (a one-time migration convenience) ---

fn ordinal_word(n: u8) -> &'static str {
    match n {
        1 => "First",
        2 => "Second",
        3 => "Third",
        4 => "Fourth",
        5 => "Fifth",
        6 => "Sixth",
        7 => "Seventh",
        _ => "",
    }
}

fn floor_name(third: u8) -> String {
    match third {
        68 => "Basement".to_string(),
        69 => "Ground Floor".to_string(),
        61..=67 => format!("{} Floor", ordinal_word(third - 60)),
        _ => String::new(),
    }
}

fn name_from_cidr(cidr: &str) -> String {
    cidr.split('.')
        .nth(2)
        .and_then(|s| s.split('/').next())
        .and_then(|s| s.trim().parse::<u8>().ok())
        .map(floor_name)
        .unwrap_or_default()
}

// --- Migration: previous bare-CIDR-ranges format ---

#[derive(Deserialize)]
struct BareConfig {
    #[serde(default)]
    buildings: Vec<BareBuilding>,
}
#[derive(Deserialize)]
struct BareBuilding {
    name: String,
    #[serde(default)]
    ranges: Vec<String>,
    #[serde(default)]
    notes: String,
}

fn migrate_bare(bare: BareConfig) -> Config {
    let buildings = bare
        .buildings
        .into_iter()
        .map(|b| Building {
            name: b.name,
            ranges: b
                .ranges
                .into_iter()
                .map(|cidr| Range {
                    name: name_from_cidr(&cidr),
                    cidr,
                })
                .collect(),
            notes: b.notes,
        })
        .collect();
    Config { buildings }
}

// --- Migration: octet-template format ---

#[derive(Deserialize)]
struct OctetConfig {
    network: OctetNetwork,
    #[serde(default)]
    levels: Vec<OctetLevel>,
    #[serde(default)]
    buildings: Vec<OctetBuilding>,
    #[serde(default)]
    subnets: Vec<String>,
}
#[derive(Deserialize)]
struct OctetNetwork {
    octets: Vec<String>,
}
#[derive(Deserialize)]
struct OctetLevel {
    label: String,
    code: u8,
}
#[derive(Deserialize)]
struct OctetBuilding {
    octet: u8,
    name: String,
    #[serde(default)]
    levels: Vec<String>,
    #[serde(default)]
    notes: String,
}

fn octet_range(octets: &[String], building_octet: u8, level_code: u8) -> Option<String> {
    if octets.len() != 4 {
        return None;
    }
    let mut o = [0u8; 4];
    for (i, tok) in octets.iter().enumerate() {
        o[i] = match tok.trim().to_ascii_lowercase().as_str() {
            "building" => building_octet,
            "level" => level_code,
            "host" => 0,
            n => n.parse::<u8>().ok()?,
        };
    }
    Some(format!("{}.{}.{}.{}/24", o[0], o[1], o[2], o[3]))
}

fn migrate_octet(legacy: OctetConfig) -> Config {
    let code_for = |label: &str| legacy.levels.iter().find(|l| l.label == label).map(|l| l.code);
    let mut buildings: Vec<Building> = legacy
        .buildings
        .into_iter()
        .map(|b| {
            let ranges = b
                .levels
                .iter()
                .filter_map(|label| code_for(label).map(|code| (label.clone(), code)))
                .filter_map(|(label, code)| {
                    octet_range(&legacy.network.octets, b.octet, code)
                        .map(|cidr| Range { name: label, cidr })
                })
                .collect();
            Building {
                name: b.name,
                ranges,
                notes: b.notes,
            }
        })
        .collect();
    if !legacy.subnets.is_empty() {
        buildings.push(Building {
            name: "Subnets".to_string(),
            ranges: legacy
                .subnets
                .into_iter()
                .map(|cidr| Range {
                    name: String::new(),
                    cidr,
                })
                .collect(),
            notes: String::new(),
        });
    }
    Config { buildings }
}

// --- Migration: original buildings array ---

#[derive(Deserialize)]
struct OldBuilding {
    octet: u8,
    name: String,
    #[serde(default)]
    basement: bool,
    #[serde(default)]
    ground: bool,
    #[serde(default)]
    floors: u8,
    #[serde(default)]
    notes: String,
}

fn migrate_array(old: Vec<OldBuilding>) -> Config {
    let buildings = old
        .into_iter()
        .map(|b| {
            let mut ranges = Vec::new();
            if b.basement {
                ranges.push(Range {
                    name: floor_name(68),
                    cidr: format!("10.{}.68.0/24", b.octet),
                });
            }
            if b.ground {
                ranges.push(Range {
                    name: floor_name(69),
                    cidr: format!("10.{}.69.0/24", b.octet),
                });
            }
            for floor in 1..=b.floors {
                let code = 60 + floor;
                ranges.push(Range {
                    name: floor_name(code),
                    cidr: format!("10.{}.{}.0/24", b.octet, code),
                });
            }
            Building {
                name: b.name,
                ranges,
                notes: b.notes,
            }
        })
        .collect();
    Config { buildings }
}

// --- Inventory + diff ---

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct Inventory {
    pub last_scan: u64,
    pub cameras: Vec<CameraRecord>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CameraRecord {
    pub ip: String,
    pub building: String,
    #[serde(default)]
    pub area: String,
    pub first_seen: u64,
    pub last_seen: u64,
}

pub struct FoundCamera {
    pub ip: String,
    pub building: String,
    pub area: String,
}

#[derive(Debug, Clone, Serialize)]
pub struct CameraStatus {
    pub ip: String,
    pub building: String,
    pub area: String,
    pub first_seen: u64,
    pub last_seen: u64,
    pub status: String,
}

pub fn load_inventory(dir: &Path) -> Inventory {
    fs::read_to_string(dir.join(INVENTORY_FILE))
        .ok()
        .and_then(|t| serde_json::from_str(&t).ok())
        .unwrap_or_default()
}

pub fn save_inventory(dir: &Path, inv: &Inventory) -> Result<(), String> {
    fs::create_dir_all(dir).map_err(|e| format!("create data dir: {e}"))?;
    let text = serde_json::to_string_pretty(inv).map_err(|e| e.to_string())?;
    fs::write(dir.join(INVENTORY_FILE), text).map_err(|e| format!("write {INVENTORY_FILE}: {e}"))
}

/// Diff a scan against the previous inventory. `scanned` is the set of addresses
/// actually probed this run: a previous camera counts as "absent" only if it was
/// in scope but not found. Cameras outside the scanned scope are retained in the
/// inventory untouched (so scanning one floor never marks the rest missing).
pub fn diff(
    prev: &Inventory,
    found: Vec<FoundCamera>,
    scanned: &HashSet<Ipv4Addr>,
    now: u64,
) -> (Inventory, Vec<CameraStatus>) {
    let prev_by_ip: HashMap<&str, &CameraRecord> =
        prev.cameras.iter().map(|c| (c.ip.as_str(), c)).collect();

    let mut found_ips: HashSet<String> = HashSet::new();
    let mut records: Vec<CameraRecord> = Vec::new();
    let mut statuses: Vec<CameraStatus> = Vec::new();

    for camera in found {
        found_ips.insert(camera.ip.clone());
        let (first_seen, status) = match prev_by_ip.get(camera.ip.as_str()) {
            Some(previous) => (previous.first_seen, "present"),
            None => (now, "new"),
        };
        let record = CameraRecord {
            ip: camera.ip,
            building: camera.building,
            area: camera.area,
            first_seen,
            last_seen: now,
        };
        statuses.push(status_of(&record, status));
        records.push(record);
    }

    for previous in &prev.cameras {
        if !found_ips.contains(&previous.ip) {
            let in_scope = previous
                .ip
                .parse::<Ipv4Addr>()
                .map(|ip| scanned.contains(&ip))
                .unwrap_or(false);
            if in_scope {
                statuses.push(status_of(previous, "absent"));
            }
            records.push(previous.clone());
        }
    }

    records.sort_by_key(|c| ip_key(&c.ip));
    statuses.sort_by_key(|c| ip_key(&c.ip));

    (
        Inventory {
            last_scan: now,
            cameras: records,
        },
        statuses,
    )
}

fn status_of(record: &CameraRecord, status: &str) -> CameraStatus {
    CameraStatus {
        ip: record.ip.clone(),
        building: record.building.clone(),
        area: record.area.clone(),
        first_seen: record.first_seen,
        last_seen: record.last_seen,
        status: status.to_string(),
    }
}

fn ip_key(ip: &str) -> u32 {
    ip.parse::<Ipv4Addr>().map(u32::from).unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn migrates_bare_ranges_and_names_floors() {
        let json = r#"{"buildings":[{"name":"Example Hall","ranges":["10.200.68.0/24","10.200.62.0/24"],"notes":""}]}"#;
        let bare: BareConfig = serde_json::from_str(json).unwrap();
        let cfg = migrate_bare(bare);
        let ranges = &cfg.buildings[0].ranges;
        assert_eq!(ranges[0].name, "Basement");
        assert_eq!(ranges[0].cidr, "10.200.68.0/24");
        assert_eq!(ranges[1].name, "Second Floor");
    }

    #[test]
    fn migrates_octet_config_using_level_labels() {
        let json = r#"{
            "network": { "octets": ["10","building","level","host"] },
            "levels": [ {"label":"2nd floor","code":62} ],
            "buildings": [ {"octet":20,"name":"Example Hall","levels":["2nd floor"],"notes":""} ],
            "subnets": []
        }"#;
        let legacy: OctetConfig = serde_json::from_str(json).unwrap();
        let cfg = migrate_octet(legacy);
        assert_eq!(cfg.buildings[0].ranges[0].name, "2nd floor");
        assert_eq!(cfg.buildings[0].ranges[0].cidr, "10.20.62.0/24");
    }

    fn found(ip: &str) -> FoundCamera {
        FoundCamera {
            ip: ip.to_string(),
            building: "Hall".to_string(),
            area: "First Floor".to_string(),
        }
    }
    fn record(ip: &str, first: u64, last: u64) -> CameraRecord {
        CameraRecord {
            ip: ip.to_string(),
            building: "Hall".to_string(),
            area: "First Floor".to_string(),
            first_seen: first,
            last_seen: last,
        }
    }

    fn scope(ips: &[&str]) -> HashSet<Ipv4Addr> {
        ips.iter().map(|s| s.parse().unwrap()).collect()
    }

    #[test]
    fn diffs_new_present_and_absent() {
        let prev = Inventory {
            last_scan: 100,
            cameras: vec![record("192.168.50.5", 100, 100), record("192.168.50.6", 100, 100)],
        };
        let scanned = scope(&["192.168.50.5", "192.168.50.6", "192.168.50.7"]);
        let (inv, statuses) = diff(&prev, vec![found("192.168.50.5"), found("192.168.50.7")], &scanned, 200);
        let by_ip: HashMap<&str, &CameraStatus> =
            statuses.iter().map(|s| (s.ip.as_str(), s)).collect();
        assert_eq!(by_ip["192.168.50.5"].status, "present");
        assert_eq!(by_ip["192.168.50.7"].status, "new");
        assert_eq!(by_ip["192.168.50.6"].status, "absent");
        assert_eq!(inv.cameras.len(), 3);
    }

    #[test]
    fn out_of_scope_camera_is_not_marked_absent() {
        let prev = Inventory {
            last_scan: 100,
            cameras: vec![record("192.168.50.5", 100, 100), record("192.168.99.9", 100, 100)],
        };
        // Only .5 was in scope this scan; .99.0.9 wasn't scanned.
        let scanned = scope(&["192.168.50.5"]);
        let (inv, statuses) = diff(&prev, vec![found("192.168.50.5")], &scanned, 200);
        let ips: Vec<&str> = statuses.iter().map(|s| s.ip.as_str()).collect();
        assert!(ips.contains(&"192.168.50.5"));
        assert!(!ips.contains(&"192.168.99.9")); // untouched, not flagged missing
        assert_eq!(inv.cameras.len(), 2); // but still retained in the inventory
    }
}
