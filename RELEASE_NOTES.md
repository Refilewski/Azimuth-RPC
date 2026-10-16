# AzimuthRPC v0.9.3

Diagnostics, Rich Presence and release packaging cleanup release.

- Standalone `AzimuthRPC.exe` now writes `AzimuthRPC.log` beside the executable when possible, with a `%LOCALAPPDATA%\AzimuthRPC\AzimuthRPC.log` fallback if the application directory is not writable.
- The standalone log records the AzimuthRPC version, Azimuth process attachment state, FH2 mapping state, Discord IPC state, Rich Presence details/state and runtime errors.
- Native FH2ML logging continues to record the detected FH2 base, Discord IPC connection state, loaded module names, Rich Presence updates and car-probe diagnostics.
- Car detection diagnostics include the `AudioEngine + 0x278` probe result, parser reason, raw ASCII and 64 raw bytes in hex whenever that probe changes.
- Empty fallback state text is not sent to Discord on screens where no useful state value is available.
- Standalone and FH2ML package versions are aligned at 0.9.3.

## Supported game build

Forza Horizon 2 Xbox One build 1.0.0.64 via Azimuth.

## Reporting issues

For standalone issues, reproduce the problem and attach `AzimuthRPC.log` from beside `AzimuthRPC.exe`. If that folder is not writable, check `%LOCALAPPDATA%\AzimuthRPC\AzimuthRPC.log`.

For FH2ML car-detection issues, attach the native `AzimuthRPC.log`; useful lines begin with `Car probe:` and include the parser result plus the raw bytes around the current car field.

## Known limitations

- The native FH2ML mod does not yet use the full FH2ML host API because the shared SDK header is not vendored in this repository.
- Native event-vs-free-roam telemetry is not yet promoted into the FH2ML DLL.
- Mainland location names currently come from coordinate anchors/coarse polygons; native road/region/landmark discovery is still research work.
