# Surveil for Windows

This directory is the beginning of the native C# port. `Surveil.Core` contains
platform-independent domain logic that will be consumed by a WinUI 3 desktop
application. The existing Tauri app remains available as the working prototype.

Implemented so far:

- Building, named-range, camera, and inventory models
- Private IPv4 CIDR parsing and expansion
- 65,534-address scan safety limit
- Duplicate and overlapping range validation
- IP-to-building/area lookup
- New, present, absent, and out-of-scope inventory comparison
- Concurrent TCP scanning with bounded concurrency, timeout, cancellation, and progress
- Atomic JSON persistence in `%LOCALAPPDATA%\Surveil`
- ONVIF WS-Discovery probing and response parsing
- xUnit coverage for the core behavior

Run the tests on Windows with:

```powershell
dotnet test native-windows/Surveil.Core.Tests/Surveil.Core.Tests.csproj
```

Next: add the WinUI 3 application shell and connect it to these services.
