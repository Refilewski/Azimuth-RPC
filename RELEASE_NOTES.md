# AzimuthRPC v0.9.1

Small diagnostics and Rich Presence cleanup release.

- Native FH2ML build now writes `AzimuthRPC.log` beside the Azimuth executable.
- The log records the AzimuthRPC version, detected FH2 base, Discord IPC connection state, loaded module names and Rich Presence updates.
- Car detection now logs the `AudioEngine + 0x278` probe result, parser reason, raw ASCII and 64 raw bytes in hex whenever that probe changes. This is intended to diagnose reports where RPC works but the current car is missing.
- Empty fallback state text is no longer sent to Discord. Screens such as Horizon Hub therefore do not show a redundant second line like `Forza Horizon 2 via AzimuthRPC` when no useful car/state value is available.
- Standalone and FH2ML package versions remain aligned at 0.9.1.

## Supported game build

Forza Horizon 2 Xbox One build 1.0.0.64 via Azimuth.

## Reporting car-detection issues

If Rich Presence works but the car is missing, reproduce the issue in-game and attach `AzimuthRPC.log` from the Azimuth directory. The useful line begins with `Car probe:` and includes the parser result plus the raw bytes around the current car field.

## Known limitations

- The native FH2ML mod does not yet use the full FH2ML host API because the shared SDK header is not vendored in this repository.
- Native event-vs-free-roam telemetry is not yet promoted into the FH2ML DLL.
- Mainland location names currently come from coordinate anchors/coarse polygons; native road/region/landmark discovery is still research work.
