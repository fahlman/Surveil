# Surveil.App — WinUI 3 front end

A desktop UI over `Surveil.Core` for discovering and bulk-provisioning ONVIF cameras.
Unpackaged WinUI 3 (Windows App SDK), MVVM via CommunityToolkit.Mvvm.

## Screens

Left-nav pages, plus **Provision** as a toggle-able right-side drawer and **Settings** in the footer.

| Page/panel | What it does                                                                 | Core API |
|------------|------------------------------------------------------------------------------|----------|
| **Buildings** | The hierarchical map **and** the inventory: a tree of building → CIDR → cameras. Check CIDRs and **Scan** them (cameras nest under their CIDR), or **Discover** the network (strays land in an **Unmapped** group). Loads the saved inventory on startup. Save / import / export JSON. | `SurveilService.ScanAsync` · `DiscoverAsync` · `JsonStore.LoadInventoryAsync` · config APIs |
| **Scan**      | Standalone TCP port sweep of IPs/CIDRs; live progress; new/online/offline vs inventory | `SurveilService.ScanAsync` |
| **Discover**  | Standalone WS-Discovery; lists ONVIF responders tagged by building/area       | `SurveilService.DiscoverAsync` |
| **Provision** *(docked right panel)* | Derive name/hostname from the map, set NTP, maximize video (codec pref, resolution-first); **dry-run** preview; truthful per-camera results | `BulkProvisioningService.{Plan,ProvisionAsync}` |
| **Settings**  | Persisted defaults (username, port/timeout/concurrency, codecs, dry-run). Never stores the password. | `SettingsStore` → settings.json |

The Provision panel is **always docked on the right**. Scan and Discover have a
**"Send to Provision →"** button that loads the found cameras into it. The building map and ONVIF
credentials are shared via `Services/AppSession`. The password is held in memory only — never
written to disk. Pages use `NavigationCacheMode=Required`, so state survives switching tabs.

An app-level `UnhandledException` handler logs to `logs/surveil.log` under the data dir and shows an
error dialog instead of crashing; view-model operations report failures in an InfoBar, not a crash.

## Prerequisites (one-time)

1. **.NET 8 SDK** (or newer) — https://dotnet.microsoft.com/download
2. **Windows App SDK / WinUI tooling**. Either:
   - Visual Studio Installer → **Modify** → *".NET Multi-platform App UI development"* **or** the
     *"Windows App SDK C# Templates"* individual component (also pulls the .NET desktop workload), or
   - CLI: `dotnet workload install --skip-manifest-update` is not needed for unpackaged apps —
     the `Microsoft.WindowsAppSDK` NuGet package supplies the build targets on restore.

## Build & run

From the repo root, after the SDK is installed:

```powershell
dotnet restore Surveil.sln
dotnet build Surveil.App/Surveil.App.csproj -c Debug -r win-x64
dotnet run   --project Surveil.App/Surveil.App.csproj -r win-x64
```

Or open `Surveil.sln` in Visual Studio, set **Surveil.App** as the startup project,
pick the **x64** platform, and press **F5**.

The app is *unpackaged* and *WindowsAppSDKSelfContained* — it bundles the Windows App
Runtime, so no separate runtime install is required to run it.

## Status

Builds clean (0 warnings / 0 errors) with .NET SDK 10.0.301 and in CI (`build-app` job).
Verified at runtime (launch + UI Automation): navigation across all pages; Buildings editor
(add/rename with live updates); Scan input validation; Settings persistence across restart;
Inventory view + quick-filter + Copy IPs + CSV export. Not yet exercised against live hardware:
the actual Scan/Discover/Provision network operations and the "Send to Provision" data hop
(both need a live network).

## Notes

- **Package versions** in `Surveil.App.csproj` (`Microsoft.WindowsAppSDK`,
  `CommunityToolkit.Mvvm`) are pinned to known-good releases. If restore can't find them,
  bump to the latest stable (VS: *Manage NuGet Packages*).
- To verify a change at runtime, see `.claude/skills/verify/SKILL.md` (build + launch +
  UI-Automation recipe).
