---
name: verify
description: Build, launch, and drive the Surveil.App WinUI 3 desktop app to verify a change at runtime (screenshots + UI Automation).
---

# Verify Surveil.App (WinUI 3 desktop)

Runtime verification for the unpackaged WinUI 3 front end. The surface is pixels + the
UI Automation tree — build it, launch the exe, drive it, screenshot what you see.

## Prerequisites
- A .NET SDK must be installed (`dotnet --list-sdks`). If none, verification is BLOCKED — the
  machine historically shipped runtimes only. Build also works in CI (`build-app` job).
- `Surveil.Core` tests run headless: `dotnet test Surveil.Core.Tests/Surveil.Core.Tests.csproj`.

## Build
```
dotnet build Surveil.App/Surveil.App.csproj -c Debug -r win-x64
```
Exe lands at `Surveil.App/bin/Debug/net8.0-windows10.0.19041.0/win-x64/Surveil.App.exe`.
It's self-contained (bundles the Windows App Runtime) — launches without a separate install.

## Launch + capture (PowerShell)
The window is often occluded by other apps, so capture it **by HWND with `PrintWindow`**
(flag `2` = PW_RENDERFULLCONTENT) rather than a screen grab — this also avoids stealing focus.
```powershell
$p = Start-Process $exe -PassThru; Start-Sleep 6
$hwnd = (Get-Process -Id $p.Id).MainWindowHandle
# PrintWindow($hwnd, hdc, 2) into a System.Drawing.Bitmap sized to GetWindowRect; save PNG.
```

## Drive it (UI Automation — works even when the window is behind others)
```powershell
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
# window: RootElement.FindFirst(Children, ProcessIdProperty == $p.Id)
# nav:    find NavigationViewItem by NameProperty ("Sites"/"Scan"/"Discover"/"Provision"),
#         it's a ListItem -> SelectionItemPattern.Select()
# fields: find by AutomationIdProperty; ValuePattern.SetValue / .Current.Value to type/read
```
Stable AutomationIds (from `x:Name`): `TargetsBox` (Scan targets), `SitesList`,
`SiteNameBox`. Add `x:Name` to any control you need to drive.

## Gotchas
- **Provision page** is the heaviest (Pivot + Expanders + PasswordBox); allow >1s to render or
  a fast capture catches a stale frame.
- **Don't trigger live actions unsupervised:** Scan/Discover hit the network; Provision writes to
  real cameras (defaults to dry-run, but don't rely on that). Verify navigation + the Sites
  editor (pure local state) instead. Confirm no `%LOCALAPPDATA%\Surveil\sites.json` was
  written if you didn't intend to Save.
- Pages use `NavigationCacheMode=Required`, so page/VM state persists across navigation — useful
  for verifying input isn't lost when switching tabs.
