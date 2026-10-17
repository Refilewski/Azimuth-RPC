# AzimuthRPC v0.9.9

Discord Rich Presence polish and car-detection diagnostics.

- "Loading Race" now appears **without a second line**: removes the generic "Forza Horizon 2 via Azimuth" state and prevents a stale car or speed from being shown while loading. This applies to both standalone EXE and native FH2ML DLL.
- The standalone log now records changes to a compact, privacy-conscious snapshot diagnostic: game UI mode, inferred activity, hook/telemetry status, event context and car ID parse result. No raw game-memory bytes are dumped.
- Car ID validation now reports a reason such as `empty`, `too-short`, `unterminated`, `invalid-character`, `missing-underscore`, or `ok` so users with missing vehicle names can provide actionable logs.
- Adds automated regression coverage for race-loading second-line suppression and snapshot diagnostics.
- Retains v0.9.8 standalone startup log fallback and the log folder shortcut, plus the v0.9.7 native startup diagnostics.

## Compatibility

Still targets FH2 Xbox One 1.0.0.64 through Azimuth. This release does **not** change memory offsets or claim confirmed compatibility with every Azimuth version or mod combination.

## Collecting useful reports

Use `%LOCALAPPDATA%\AzimuthRPC\logs\AzimuthRPC.log` (or the standalone fallback `%TEMP%\AzimuthRPC\AzimuthRPC.log`). Include the **Snapshot diagnostic:** lines with your Azimuth version, other mods such as Extra Options, and whether you used the EXE or FH2ML DLL. Please review/redact any sensitive data before sharing.
