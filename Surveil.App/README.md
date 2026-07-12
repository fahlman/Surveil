# Surveil.App — WinUI 3 front end

A desktop UI over `Surveil.Core` for discovering and bulk-provisioning ONVIF cameras.
Unpackaged WinUI 3 (Windows App SDK), MVVM via CommunityToolkit.Mvvm.

## Screens

| Page       | What it does                                                                 | Core API |
|------------|------------------------------------------------------------------------------|----------|
| **Buildings** | Edit the building map (buildings → CIDR ranges); save / import / export JSON | `SurveilService.{Get,Save,Import,Export}ConfigAsync` |
| **Scan**      | TCP port sweep of IPs/CIDRs; live progress; new/online/offline vs inventory  | `SurveilService.ScanAsync` |
| **Discover**  | WS-Discovery multicast; lists ONVIF responders tagged by building/area       | `SurveilService.DiscoverAsync` |
| **Provision** | Derive name/hostname from the map, set NTP, maximize video (codec pref, resolution-first); **dry-run** preview; truthful per-camera results | `BulkProvisioningService.{Plan,ProvisionAsync}` |

The building map and ONVIF credentials are shared across pages via `Services/AppSession`.
The password is held in memory only — it is never written to disk.

## Prerequisites (one-time)

This machine currently has only .NET **runtimes** installed — no SDK — so nothing here
builds yet. To build/run you need:

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

## Notes / likely first-build tweaks

- **Package versions** in `Surveil.App.csproj` (`Microsoft.WindowsAppSDK`,
  `CommunityToolkit.Mvvm`) are pinned to known-good releases. If restore can't find them,
  bump to the latest stable (VS: *Manage NuGet Packages*).
- These files were authored without a local compiler (no SDK present). Expect a small
  amount of first-build fixup — most likely around NuGet versions or a XAML binding detail.
