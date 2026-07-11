mod config;
mod discovery;
mod store;
mod wsdiscovery;

use std::collections::HashSet;
use std::path::PathBuf;
use std::time::{SystemTime, UNIX_EPOCH};

use tauri::{Emitter, Manager};
use tauri_plugin_dialog::DialogExt;

use config::Config;
use store::{CameraStatus, FoundCamera};

/// The sample config that ships inside the binary and seeds app-data on first
/// launch. Sample buildings with reserved documentation ranges only — nothing
/// real, and nothing that resolves to a scannable (private) address.
const SEED_CONFIG: &str = include_str!("../seed/buildings.json");

#[derive(Clone, serde::Serialize)]
struct ProgressPayload {
    scanned: usize,
    total: usize,
    found: usize,
}

fn now_secs() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0)
}

fn data_dir(app: &tauri::AppHandle) -> Result<PathBuf, String> {
    app.path()
        .app_data_dir()
        .map_err(|e| format!("app data dir: {e}"))
}

fn enrich(config: &Config, ip: std::net::Ipv4Addr) -> FoundCamera {
    let (building, area) = config
        .locate(ip)
        .map(|(b, a)| (b.to_string(), a.to_string()))
        .unwrap_or_default();
    FoundCamera {
        ip: ip.to_string(),
        building,
        area,
    }
}

/// Load the config (seeding app-data from the embedded sample if needed, and
/// migrating older formats forward).
#[tauri::command]
fn get_config(app: tauri::AppHandle) -> Result<Config, String> {
    store::load_config(&data_dir(&app)?, SEED_CONFIG)
}

/// Validate and persist the building configuration.
#[tauri::command]
fn save_config(app: tauri::AppHandle, config: Config) -> Result<(), String> {
    config.validate()?;
    store::save_config(&data_dir(&app)?, &config)
}

/// Export the current config using the platform's native Save dialog.
#[tauri::command]
async fn export_config(app: tauri::AppHandle) -> Result<Option<String>, String> {
    let config = store::load_config(&data_dir(&app)?, SEED_CONFIG)?;
    let Some(selected) = app
        .dialog()
        .file()
        .set_file_name("surveil-config.json")
        .add_filter("JSON", &["json"])
        .blocking_save_file()
    else {
        return Ok(None);
    };
    let path = selected
        .into_path()
        .map_err(|e| format!("invalid export path: {e}"))?;
    let text = serde_json::to_string_pretty(&config).map_err(|e| e.to_string())?;
    std::fs::write(&path, text).map_err(|e| format!("write export: {e}"))?;
    Ok(Some(path.to_string_lossy().into_owned()))
}

/// Scan every building's ranges, diff against the stored inventory, persist, and
/// return each camera tagged new/present/absent. Emits `scan:progress`.
#[tauri::command]
async fn scan(
    app: tauri::AppHandle,
    window: tauri::Window,
    targets: Vec<String>,
    port: u16,
    concurrency: usize,
    timeout: u64,
) -> Result<Vec<CameraStatus>, String> {
    let dir = data_dir(&app)?;
    let config = store::load_config(&dir, SEED_CONFIG)?;
    let addresses = config::expand_private(&targets)?;
    let scanned_scope: HashSet<std::net::Ipv4Addr> = addresses.iter().copied().collect();

    let found_ips = discovery::sweep(
        addresses,
        port,
        concurrency,
        timeout,
        |scanned, total, found| {
            let _ = window.emit(
                "scan:progress",
                ProgressPayload {
                    scanned,
                    total,
                    found,
                },
            );
        },
    )
    .await;

    let found: Vec<FoundCamera> = found_ips
        .into_iter()
        .map(|ip| enrich(&config, ip))
        .collect();

    let now = now_secs();
    let previous = store::load_inventory(&dir);
    let (updated, statuses) = store::diff(&previous, found, &scanned_scope, now);
    store::save_inventory(&dir, &updated)?;

    Ok(statuses)
}

#[derive(serde::Serialize)]
struct WsResponder {
    ip: String,
    building: String,
    area: String,
    xaddrs: String,
}

#[derive(serde::Serialize)]
struct WsDiscoveryResult {
    responders: Vec<WsResponder>,
    distinct_subnets: usize,
    distinct_buildings: usize,
}

/// Fire one ONVIF WS-Discovery probe and report who answers — a diagnostic for
/// whether the network forwards discovery multicast across subnets.
#[tauri::command]
async fn ws_discover(app: tauri::AppHandle, timeout: u64) -> Result<WsDiscoveryResult, String> {
    let config = store::load_config(&data_dir(&app)?, SEED_CONFIG)?;
    let responders = wsdiscovery::probe(timeout).await?;

    let mut subnets = HashSet::new();
    let mut buildings = HashSet::new();
    let list = responders
        .into_iter()
        .map(|r| {
            let o = r.ip.octets();
            subnets.insert((o[0], o[1], o[2]));
            let (building, area) = config
                .locate(r.ip)
                .map(|(b, a)| (b.to_string(), a.to_string()))
                .unwrap_or_default();
            if !building.is_empty() {
                buildings.insert(building.clone());
            }
            WsResponder {
                ip: r.ip.to_string(),
                building,
                area,
                xaddrs: r.xaddrs,
            }
        })
        .collect();

    Ok(WsDiscoveryResult {
        responders: list,
        distinct_subnets: subnets.len(),
        distinct_buildings: buildings.len(),
    })
}

/// A building name the Building Generator should jump to editing on open.
struct PendingEdit(std::sync::Mutex<Option<String>>);

/// Open (or focus) the Building Generator window. Returns true if it already
/// existed.
fn show_configurator(app: &tauri::AppHandle) -> Result<bool, String> {
    if let Some(existing) = app.get_webview_window("buildings") {
        existing.set_focus().map_err(|e| e.to_string())?;
        return Ok(true);
    }
    tauri::WebviewWindowBuilder::new(
        app,
        "buildings",
        tauri::WebviewUrl::App("buildings.html".into()),
    )
    .title("Building Generator")
    .inner_size(760.0, 820.0)
    .min_inner_size(560.0, 500.0)
    .build()
    .map_err(|e| e.to_string())?;
    Ok(false)
}

#[tauri::command]
fn open_configurator(app: tauri::AppHandle) -> Result<(), String> {
    show_configurator(&app).map(|_| ())
}

/// Open the Building Generator focused on editing one building.
#[tauri::command]
fn edit_building(
    app: tauri::AppHandle,
    state: tauri::State<PendingEdit>,
    name: String,
) -> Result<(), String> {
    *state.0.lock().map_err(|e| e.to_string())? = Some(name.clone());
    // Newly-created windows read the pending edit on load; an already-open one
    // needs a nudge.
    if show_configurator(&app)? {
        app.emit("edit-building", name).map_err(|e| e.to_string())?;
    }
    Ok(())
}

/// The Building Generator calls this on load to pick up (and clear) a pending
/// "edit this building" request.
#[tauri::command]
fn take_pending_edit(state: tauri::State<PendingEdit>) -> Option<String> {
    state.0.lock().ok().and_then(|mut p| p.take())
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .manage(PendingEdit(std::sync::Mutex::new(None)))
        .invoke_handler(tauri::generate_handler![
            get_config,
            save_config,
            export_config,
            scan,
            ws_discover,
            open_configurator,
            edit_building,
            take_pending_edit
        ])
        .run(tauri::generate_context!())
        .expect("error while running Surveil");
}
