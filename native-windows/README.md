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
- ONVIF Media2 profile and video-encoder configuration client
- Supported resolution, frame-rate, bitrate, and quality option discovery
- Validated persistent video-encoder updates using the current `ver20` Media2 contract
- Device Management `GetServices` negotiation with automatic Media2 preference
- Media1/Profile S fallback for older cameras using the same video configuration interface
- UI-ready authenticated camera connection through `SurveilService`
- Capability-validated brightness and automatic/manual white-balance configuration
- Device hostname and NTP/manual date-time configuration
- Computer-time-zone detection with Fort Wayne/Windows Eastern mapped to `EST5EDT,M3.2.0,M11.1.0`
- Verified hostname updates with DHCP hostname override disabled
- Friendly camera-name scopes that preserve fixed and unrelated configurable scopes
- Validated Media2 codec selection using the camera's canonical encoding name
- Explicit authentication-failure classification for UI error reporting
- Migration of all three legacy Surveil configuration formats
- Import and export services
- A UI-ready `SurveilService` coordinating scans, inventory, location, and discovery
- Injectable scanner and discovery interfaces for testing and future UI previews
- xUnit coverage for the core behavior

Run the tests on Windows with:

```powershell
dotnet test native-windows/Surveil.Core.Tests/Surveil.Core.Tests.csproj
```

The ONVIF clients use HTTP Digest authentication through .NET and accept an injectable
`HttpClient` for testing. Next ONVIF work: add WS-Security UsernameToken authentication for
cameras that require it.

Next UI work: add the WinUI 3 application shell and connect it to `SurveilService`.
