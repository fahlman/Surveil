# Surveil

A desktop tool to discover, inventory, and manage IP security cameras across a
large network. It sweeps every camera subnet, identifies each camera by site
and floor, and diffs each scan against the last to surface **new** and
**missing** cameras — change detection for a fleet that's painful to track
through a VMS.

## Stack

Surveil is a native **C# / .NET** application for Windows.

- **`Surveil.Core`** — platform-independent domain logic (targets `net8.0`,
  covered by xUnit tests).
- **WinUI 3 desktop app** *(in progress)* — the UI shell that consumes
  `Surveil.Core`.

> An earlier Tauri/Rust prototype was removed; the C# port is the sole
> implementation going forward.

## Implemented so far

- Site, named-range, camera, and inventory models
- Private IPv4 CIDR parsing and expansion, with a 65,534-address scan safety limit
- Duplicate and overlapping range validation
- IP-to-site/area lookup
- New, present, absent, and out-of-scope inventory comparison
- Concurrent TCP scanning with bounded concurrency, timeout, cancellation, and progress
- Atomic JSON persistence in `%LOCALAPPDATA%\Surveil`
- ONVIF WS-Discovery probing and response parsing
- ONVIF Media2 profile and video-encoder configuration client, with resolution,
  frame-rate, bitrate, and quality option discovery
- Validated persistent video-encoder updates against the current `ver20` Media2 contract
- Device Management `GetServices` negotiation with automatic Media2 preference
- Media1/Profile S fallback for older cameras via the same video-configuration interface
- Capability-validated brightness and automatic/manual white-balance configuration
- Device hostname, NTP/manual date-time, and friendly camera-name scope configuration
- Explicit authentication-failure classification for UI error reporting
- Migration of all three legacy Surveil configuration formats, plus import/export
- A UI-ready `SurveilService` coordinating scans, inventory, location, and discovery

## Data

Surveil stores two JSON files in `%LOCALAPPDATA%\Surveil`:

- `sites.json` — the site map (name and named private network ranges),
  editable in the app.
- `cameras.json` — the camera inventory with first/last-seen timestamps, updated
  after every scan.

## Network configuration

Each installation defines its own sites, named areas, and private CIDR
ranges. No organization-specific network layout is compiled into Surveil.

## Scanning

Select any combination of sites and named ranges, then scan them on the
chosen TCP port. Surveil limits each scan to 65,534 unique private addresses to
guard against accidentally selecting an overly broad range.

## Develop

```powershell
dotnet test Surveil.Core.Tests/Surveil.Core.Tests.csproj
dotnet test Surveil.App.Tests/Surveil.App.Tests.csproj
dotnet build Surveil.App/Surveil.App.csproj -c Debug -r win-x64
```

Set `SURVEIL_DEMO=1` before launching a Debug build to populate the Sites page with
development-only camera fixtures. Normal launches always load the persisted inventory.

The ONVIF clients use HTTP Digest authentication through .NET and accept an
injectable `HttpClient` for testing.

- **Next ONVIF work:** WS-Security UsernameToken authentication for cameras that require it.
- **Next UI work:** the WinUI 3 application shell, connected to `Surveil.Core`.
