# AzimuthRPC v0.9.7

Diagnostics and compatibility troubleshooting release.

- FH2ML native DLL now writes an initialization trace **before validating the FH2ML Host API**. An incompatible host API, missing callback or unsupported FH2 image can no longer fail without an AzimuthRPC startup entry.
- Reports the exact startup rejection reason in the local diagnostic log, including FH2ML API version mismatch and rejected game module.
- Corrected public and native documentation of the log location.
- Added a CI contract check that protects early startup diagnostics.

**This update does not change the car offsets or claim verified compatibility with Azimuth 0.9.10.** Please use these logs to investigate reports of missing car detection and compare results with/without other FH2 mods.

The standalone app's runtime behavior is unchanged apart from the shared version bump.

## Supported game build

Forza Horizon 2 Xbox One build 1.0.0.64 via Azimuth. Compatibility with individual Azimuth / FH2ML versions must be tested.

## Reporting issues

Both the standalone EXE and native FH2ML DLL write logs to `%LOCALAPPDATA%\AzimuthRPC\logs\AzimuthRPC.log`; the rotated previous log is `AzimuthRPC.log.1`. Native diagnostics may also be mirrored to the FH2ML host log. If no log is created, verify that the EXE launches or that the host loads the DLL and invokes `FH2ML_Initialize`.

When reporting car detection problems, specify the Azimuth version, EXE vs DLL, FH2ML version if applicable, other active mods (particularly Extra Options), and whether Discord Rich Presence itself connects. Review logs for sensitive data before posting.
