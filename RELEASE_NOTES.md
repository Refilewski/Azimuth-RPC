# AzimuthRPC v0.9.4

Small standalone packaging and diagnostics fix release.

- The standalone `AzimuthRPC.exe` now embeds its application icon directly in the published executable.
- Standalone diagnostics logging writes `AzimuthRPC.log` beside the executable when possible, with a `%LOCALAPPDATA%\AzimuthRPC\AzimuthRPC.log` fallback if the application directory is not writable.
- Fixed a standalone logger naming collision that could break the build after the logging changes.
- Release notes are now validated against the project version in CI to prevent publishing stale notes under a newer tag.
- Standalone and FH2ML package versions are aligned at 0.9.4.

## Supported game build

Forza Horizon 2 Xbox One build 1.0.0.64 via Azimuth.

## Reporting issues

For standalone issues, reproduce the problem and attach `AzimuthRPC.log` from beside `AzimuthRPC.exe`. If that folder is not writable, check `%LOCALAPPDATA%\AzimuthRPC\AzimuthRPC.log`.

For FH2ML car-detection issues, attach the native `AzimuthRPC.log`; useful lines begin with `Car probe:` and include the parser result plus the raw bytes around the current car field.

## Known limitations

- The native FH2ML mod does not yet use the full FH2ML host API because the shared SDK header is not vendored in this repository.
- Native event-vs-free-roam telemetry is not yet promoted into the FH2ML DLL.
- Mainland location names currently come from coordinate anchors/coarse polygons; native road/region/landmark discovery is still research work.
