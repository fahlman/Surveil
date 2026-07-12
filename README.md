# Surveil

A desktop tool to discover, inventory, and manage IP security cameras across a
large network. It sweeps every camera subnet, identifies each camera by building
and floor, and diffs each scan against the last to surface **new** and
**missing** cameras — change detection for a fleet that's painful to track
through a VMS.

## Stack

Surveil is a native **C# / .NET** application for Windows.

- **`native-windows/Surveil.Core`** — platform-independent domain logic: subnet
  sweep, building-map scanning, scan-to-scan diff, ONVIF discovery and camera
  configuration, and atomic JSON persistence. Targets `net8.0` and is covered by
  xUnit tests.
- **WinUI 3 desktop app** *(in progress)* — the UI shell that consumes
  `Surveil.Core`.

See [`native-windows/README.md`](native-windows/README.md) for the full feature
list and build instructions.

> An earlier Tauri/Rust prototype was removed; the C# port is the sole
> implementation going forward.

## Data

Surveil stores two JSON files in `%LOCALAPPDATA%\Surveil`:

- `buildings.json` — the building map (name and named private network ranges),
  editable in the app.
- `cameras.json` — the camera inventory with first/last-seen timestamps, updated
  after every scan.

## Network configuration

Each installation defines its own buildings, named areas, and private CIDR
ranges. No organization-specific network layout is compiled into Surveil.

## Scanning

Select any combination of buildings and named ranges, then scan them on the
chosen TCP port. Surveil limits each scan to 65,534 unique private addresses to
guard against accidentally selecting an overly broad range.

## Develop

```powershell
dotnet test native-windows/Surveil.Core.Tests/Surveil.Core.Tests.csproj
```
