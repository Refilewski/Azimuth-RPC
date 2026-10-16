# AzimuthRPC v0.9.6

FH2ML SDK integration for the native mod.

- The FH2ML native DLL now consumes the official FH2ML Host API v1 instead of ignoring the initializer argument.
- Registers `azimuth-rpc` and its version through `RegisterMod` during native initialization.
- Mirrors native diagnostics into the FH2ML host log through `Log` while keeping the existing AzimuthRPC log.
- Uses `GetGameModule()` as the authoritative FH2 base address and no longer scans the process address space with `VirtualQuery`.
- Validates the FH2ML API table and the supported FH2 1.0.0.64 PE image before starting the RPC worker.
- Vendors the MIT-licensed FH2ML SDK header used to build the native DLL.
- Adds a CI contract check so the host-API integration cannot silently regress back to process scanning.

The standalone app behavior is unchanged apart from the shared version bump.

## Supported game build

Forza Horizon 2 Xbox One build 1.0.0.64 via Azimuth.

## Reporting issues

Logs are stored at `%LOCALAPPDATA%\AzimuthRPC\logs\AzimuthRPC.log`. If rotation occurs, the previous log is retained as `AzimuthRPC.log.1`. Native FH2ML diagnostics are also mirrored into the FH2ML log.