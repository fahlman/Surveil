# Surveil

A desktop tool to discover, inventory, and manage IP security cameras across a
large network. It sweeps every camera subnet, identifies each camera by building
and floor, and diffs each scan against the last to surface **new** and
**missing** cameras — change detection for a fleet that's painful to track
through a VMS.

## Stack

- **Tauri v2** — native desktop app, small self-contained binary.
- **Rust backend** (`src-tauri/`) — the engine: subnet sweep, building-map
  scanning, scan-to-scan diff, JSON persistence in app-data.
- **Vanilla HTML/CSS/JS frontend** (`src/`) — no Node, no bundler. Calls Rust
  over Tauri's `invoke` (`window.__TAURI__`, via `withGlobalTauri`).

## Data

Two JSON files live in the OS app-data folder
(`~/Library/Application Support/com.fahlsing.surveil/` on macOS,
`%APPDATA%\com.fahlsing.surveil\` on Windows):

- `buildings.json` — the building map (number, name, abbreviation, floors),
  seeded from an embedded default on first launch and editable in the app.
- `cameras.json` — the camera inventory with first/last-seen timestamps, updated
  after every scan.

## Network configuration

Each installation defines its own buildings, named areas, and private CIDR
ranges in the Building Generator. No organization-specific network layout is
compiled into Surveil.

## Scan modes

- **Thorough** (default) — probe every building on the whole fleet's VLAN set,
  so a camera misconfigured onto the wrong VLAN still gets found and flagged.
- **Fast** — probe each building only on the VLANs it actually has.

## Develop (macOS)

```sh
cd src-tauri
cargo tauri dev                       # live-reload window
cargo tauri build --debug --bundles app   # produce Surveil.app
```

## Ship to Windows

The Windows installer is built by GitHub Actions on a Windows runner (Tauri
can't cross-build Windows from macOS). Win11 already ships the WebView2 runtime.

## Tests

```sh
cargo test --manifest-path src-tauri/Cargo.toml
```
