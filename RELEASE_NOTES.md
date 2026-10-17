# AzimuthRPC v0.9.8

Standalone EXE and FH2ML DLL diagnostic reliability update.

- The standalone EXE now shows a startup error if logging cannot be initialized instead of exiting without explanation.
- If the standard AppData log is unavailable, standalone logging falls back to `%TEMP%\AzimuthRPC\AzimuthRPC.log` and tells the user where it is.
- Added an **Open log folder** item to the Windows system-tray menu and a clear notice when AzimuthRPC is already running.
- The CI standalone smoke test now verifies the creation of a real startup log, not just the process exit code.
- Keeps the v0.9.7 native DLL diagnostics: `FH2ML_Initialize` records startup before checking the host API, including a reason for incompatible API, missing callbacks, and unsupported FH2 image checks.
- Corrected the documented log path.

**This update does not modify car offsets or prove compatibility with Azimuth 0.9.10.** It's designed to help investigate installations where the car does not show up or diagnostics never appear.

## Logs

Standard location for both EXE and FH2ML DLL: `%LOCALAPPDATA%\AzimuthRPC\logs\AzimuthRPC.log`. The previous log is rotated as `AzimuthRPC.log.1` when needed.

Standalone fallback when AppData cannot be used: `%TEMP%\AzimuthRPC\AzimuthRPC.log`.

If no log appears even with v0.9.8, check whether the EXE actually launches (or is already running in the tray), or whether the FH2ML loader calls the DLL initializer. Include your exact Azimuth version, FH2ML version if applicable, EXE vs DLL, active mods (especially Extra Options) and screenshots of any startup errors in your report. Review logs for sensitive info before sharing.

## Supported game build

Forza Horizon 2 Xbox One build 1.0.0.64 via Azimuth. Compatibility with individual Azimuth/FH2ML versions requires live testing.
