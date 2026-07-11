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

- `buildings.json` — the building map (name and named private network ranges),
  seeded from an embedded default on first launch and editable in the app.
- `cameras.json` — the camera inventory with first/last-seen timestamps, updated
  after every scan.

## Network configuration

Each installation defines its own buildings, named areas, and private CIDR
ranges in the Building Generator. No organization-specific network layout is
compiled into Surveil.

## Scanning

Select any combination of buildings and named ranges, then scan them on the
chosen TCP port. Surveil limits each scan to 65,534 unique private addresses to
guard against accidentally selecting an overly broad range.

## Develop (macOS)

```sh
cd src-tauri
cargo tauri dev                       # live-reload window
cargo tauri build --debug --bundles app   # produce Surveil.app
```

## Ship to Windows

GitHub Actions tests the project and builds NSIS and MSI installers on a Windows
runner after every push to `main`. Download them from the workflow's
`Surveil-Windows` artifact. Tauri can't cross-build Windows installers from
macOS; Windows 11 already includes the WebView2 runtime.

## Tests

```sh
cargo test --manifest-path src-tauri/Cargo.toml
```
