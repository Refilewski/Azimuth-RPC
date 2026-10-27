# Architecture

## Runtime boundaries

AzimuthRPC now has two production-facing runtime targets plus developer tooling.

### Managed standalone

`AzimuthRPC.Core` targets .NET 8 and owns the shared managed snapshot/state/presence model. `IMemoryReader` requires exact reads or failure. `GameSnapshotReader` parses vehicle bytes and guarded UI state; `GameStateInterpreter` combines memory and telemetry; `DiscordPresenceBuilder` and `DiscordIpcClient` build and publish presence.

`AzimuthRPC.App` is the public tray-only standalone host. It discovers Azimuth/FH2, validates the supported PE image, reads the game externally and publishes Discord Rich Presence.

`AzimuthRPC.Debug` remains developer tooling. It owns the WPF UI, process discovery, external read handle, research scanners/dumps and the existing external telemetry hooks.

### Native FH2 Mod Platform mod

`native/FH2ML.AzimuthRPC` is a separate native C++ x64 runtime which does not host .NET. It exports `fh2mod_init`, uses the vendored MIT-licensed FH2 Mod Platform header, registers with the host and gets the FH2 image base via `game_base()`. Initialization is not blocked by game load timing: the worker waits for Azimuth to map the supported PE image. The native runtime performs guarded in-process reads and writes Discord IPC. It does not install gameplay hooks.

Both native and managed car readers prefer the historically observed `AudioEngine+0x278` field. If that fails, a uniquely plausible ID within a bounded portion of the same audio object can serve as a fallback. The fallback is **not** a confirmed Azimuth 1.0.2 offset and must be validated in-game.

### Managed InProcess experiments

`AzimuthRPC.InProcess` is still a managed guarded-read adapter experiment. `NativeBootstrapImplemented` remains false. It should not be confused with the working native FH2 Mod Platform C++ plugin.

## Snapshot/read model

The managed reader uses bounded, guarded reads and revalidates roots before publishing a snapshot. One vehicle sample supplies the current car, speed, XYZ/mirror and velocity data. Unsupported PE layouts fail closed.

The native FH2 Mod Platform implementation follows the same conservative principle: validate the supported build, validate pointer/data ranges and publish only coherent data.

Neither runtime claims an atomic game transaction; the game may continue changing while a sample is taken.

## State and presence

Current runtime state detection covers:

- Horizon Hub;
- Garage;
- Loading;
- Pause Menu;
- Photo Mode;
- World Map;
- in-world fallback from coherent vehicle telemetry.

The standalone debug path also retains external event-context / normalized-rev hooks used for research and richer activity interpretation. Those hooks are not currently installed by the native FH2 Mod Platform DLL.

Mainland location presence currently comes from player coordinates plus known anchors/coarse polygons. It is a pragmatic resolver, not a discovered native road/region/landmark source. Native location discovery stays under `docs/research/` until live evidence is strong enough to replace that resolver.

## Concurrency and shutdown

The standalone worker owns mutable process/IPC resources and clears state on detach/restart. Debug hooks verify ownership before restoration and remain best-effort because safe cross-mod hot-unload is not proven.

The native DLL performs substantial work outside `DllMain`; `fh2mod_init` starts the runtime worker. No hot-unload contract is claimed yet.

## Repository/test boundaries

Regression memory captures that are required by automated tests live under `tests/fixtures/`. New ad-hoc developer captures belong in ignored local `dumps/` output rather than the repository root.

Research notes, failed hypotheses and native-location investigation live under `docs/research/`. Production code should only consume offsets/chains that have enough live evidence to fail safely.

## Next native work

The next useful native steps are:

1. verify car-ID recovery in live Azimuth 1.0.2 and replace the heuristic with a confirmed source when available;
2. add validated event/free-roam telemetry to the native runtime without introducing unsafe hook ownership;
3. replace coarse location polygons only when a real FH2 road/region/landmark source is identified and live-validated;
4. define an explicit unload/shutdown contract before attempting hot unload.
