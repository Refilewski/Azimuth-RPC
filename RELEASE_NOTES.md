# AzimuthRPC v0.9.5

Reliability and diagnostics cleanup for both the standalone app and FH2ML native mod.

- Throttled Discord Rich Presence updates to a minimum 4-second interval while still sending the latest pending state.
- Moved standalone and native logs to `%LOCALAPPDATA%\AzimuthRPC\logs\AzimuthRPC.log`.
- Logs now rotate to `AzimuthRPC.log.1` after 2 MB instead of deleting previous diagnostics.
- Fixed the FH2ML native version string so it matches the packaged mod version.
- CI now validates the standalone, manifest and native C++ versions together.
- Removed the unused public `rpc-config.example.json` file; the standalone app does not expose file-based configuration.
- Kept native teardown non-blocking to match FH2ML ABI v1, which has no hot-unload contract.

## Supported game build

Forza Horizon 2 Xbox One build 1.0.0.64 via Azimuth.

## Reporting issues

Logs are stored at `%LOCALAPPDATA%\AzimuthRPC\logs\AzimuthRPC.log`. If rotation occurs, the previous log is retained as `AzimuthRPC.log.1`.
