# Architecture

## Runtime boundaries

AzimuthRPC now has two production-facing runtime targets plus developer tooling.

### Managed standalone

`AzimuthRPC.Core` targets .NET 8 and owns the shared managed snapshot/state/presence model. `IMemoryReader` requires exact reads or failure. `GameSnapshotReader` parses vehicle bytes and guarded UI state; `GameStateInterpreter` combines memory and telemetry; `DiscordPresenceBuilder` and `DiscordIpcClient` build and publish presence.

`AzimuthRPC.App` is the public tray-only standalone host. It discovers Azimuth/FH2, validates the supported PE image, reads the game externally and publishes Discord Rich Presence.

`AzimuthRPC.Debug` remains developer tooling. It owns the WPF UI, process discovery, external read handle, research scanners/dumps and the existing external telemetry hooks.

### Native FH2ML mod

`native/FH2ML.AzimuthRPC` is a separate native x64 C++ runtime. It does not host Core or the .NET runtime. It exports `FH2ML_Initialize`, validates the supported FH2 image, performs direct in-process reads and talks to Discord IPC itself.

The current FH2ML host API pointer is deliberately treated as opaque because the shared `sdk/FH2ML.h` contract is not vendored here yet. No gameplay patch or hook is installed by the native RPC MVP.

### Managed InProcess experiments

`AzimuthRPC.InProcess` is still a managed guarded-read adapter experiment. `NativeBootstrapImplemented` remains false. It should not be confused with the working native FH2ML C++ plugin.

## Snapshot/read model

The managed reader uses bounded, guarded reads and revalidates roots before publishing a snapshot. One vehicle sample supplies the current car, speed, XYZ/mirror and velocity data. Unsupported PE layouts fail closed.

The native FH2ML implementation follows the same conservative principle: validate the supported build, validate pointer/data ranges and publish only coherent data.

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

The standalone debug path also retains external event-context / normalized-rev hooks used for research and richer activity interpretation. Those hooks are not currently installed by the native FH2ML DLL.

Mainland location presence currently comes from player coordinates plus known anchors/coarse polygons. It is a pragmatic resolver, not a discovered native road/region/landmark source. Native location discovery stays under `docs/research/` until live evidence is strong enough to replace that resolver.

## Concurrency and shutdown

The standalone worker owns mutable process/IPC resources and clears state on detach/restart. Debug hooks verify ownership before restoration and remain best-effort because safe cross-mod hot-unload is not proven.

The native DLL performs substantial work outside `DllMain`; `FH2ML_Initialize` starts the runtime worker. No hot-unload contract is claimed yet.

## Repository/test boundaries

Regression memory captures that are required by automated tests live under `tests/fixtures/`. New ad-hoc developer captures belong in ignored local `dumps/` output rather than the repository root.

Research notes, failed hypotheses and native-location investigation live under `docs/research/`. Production code should only consume offsets/chains that have enough live evidence to fail safely.

## Next native work

The next useful native steps are:

1. vendor/share the real FH2ML SDK header and use the versioned host API without guessing ABI layouts;
2. add validated event/free-roam telemetry to the native runtime without introducing unsafe hook ownership;
3. replace coarse location polygons only when a real FH2 road/region/landmark source is identified and live-validated;
4. define an explicit unload/shutdown contract before attempting hot unload.
