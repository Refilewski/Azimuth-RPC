# AzimuthRPC v1.0.0

**Vehicle detection fix — first stable 1.x release.**

Thank you to the FH2 community testers who reproduced the missing-car issue and confirmed the PR #5 standalone test build resolves it in-game.

## Fixed

- Car detection could stop updating after changing vehicles or returning from a race while Discord and speed telemetry continued to work (`CarProbe=invalid-character`).
- The car ID reader previously treated `AudioEngine+0x278` as inline text only. It now also supports a guarded, MSVC-style heap-backed string candidate used when an identifier exceeds the short-string capacity.
- Failed reads no longer reuse a previously valid car name. Memory validation requires sensible pointer/size/capacity metadata, valid NUL-terminated ASCII IDs, and repeated consistent reads before publishing a heap ID.
- Tests cover long-to-long vehicle changes, returning to a short ID, unreadable or malformed memory, and changes while sampling.

## Available builds

- **AzimuthRPC.exe:** standalone Windows x64 application, with the community-tested fix.
- **FH2ML-AzimuthRPC-1.0.0.zip:** native mod package with the same guarded string reader. The reported live success was for the standalone EXE; native behavior and other mod combinations still warrant independent testing.

## Compatibility and diagnostics

Targets **Forza Horizon 2 Xbox One 1.0.0.64** through Azimuth. This release does **not** add new game memory offsets, require Extra Options, or include the experimental game-object method from PR #6.

The `CarProbe` diagnostic can show `ok` (inline), `ok-heap` (heap-backed), `heap-*` (guarded failure), or `invalid-character`. Report wrong or missing car names with the selected car, game/mod versions, and sanitized logs via [Issue #4](https://github.com/Refilewski/Azimuth-RPC/issues/4).

Please review logs before sharing them; they may include local paths. Standalone logs: `%LOCALAPPDATA%\AzimuthRPC\logs\AzimuthRPC.log` (fallback `%TEMP%\AzimuthRPC\AzimuthRPC.log`).

## Previous improvements

Includes v0.9.9 race-loading presence cleanup, car-probe diagnostics, startup logging and the supported location, speed and activity presence functionality.
