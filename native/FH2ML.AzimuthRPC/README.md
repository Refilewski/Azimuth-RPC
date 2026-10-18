# AzimuthRPC as an FH2ML native mod

This directory builds the native x64 `AzimuthRPC.dll` intended to be loaded by FH2ML as a manifest mod.

The implementation is deliberately native C++ and does **not** host the .NET runtime inside Azimuth.

## Current runtime

The DLL:

- exports `FH2ML_Initialize`;
- starts a worker thread after initialization instead of doing RPC work in `DllMain`;
- validates the mapped FH2 Xbox One 1.0.0.64 PE identity;
- reads live car, speed, XYZ and velocity from the known game structures;
- reproduces the current Hub / Garage / Loading / Pause / Photo Mode / World Map detection;
- resolves mainland macro locations from live coordinates using the current anchor/polygon resolver;
- connects directly to Discord's local `discord-ipc-*` named pipes;
- publishes car and rounded speed while the conservative in-world fallback is positive;
- writes a capped diagnostic log for public bug reports.

No gameplay patch or hook is installed by this runtime. Event-vs-free-roam telemetry and native road/region discovery are intentionally still separate research work.

## Diagnostics

The native mod writes (paste this path into Windows Explorer):

```text
%LOCALAPPDATA%\AzimuthRPC\logs\AzimuthRPC.log
```

The log starts as soon as `FH2ML_Initialize` is called, including early failures caused by an incompatible FH2ML API or unsupported FH2 game image. If there is no log, confirm the mod DLL was loaded and its initializer invoked.

The log rotates after roughly 2 MiB, keeping the previous log as `AzimuthRPC.log.1`.

At startup it records the AzimuthRPC version, loaded module names, the detected FH2 image base and Discord IPC connection state. When the car probe changes it also records:

- parser result (`ok`, `empty`, `too-short`, `unterminated`, `missing-underscore`, or the invalid character position);
- the `AudioEngine` pointer and `+0x278` car-field offset;
- current UI mode, speed and XYZ when available;
- 64 bytes from the car field as printable ASCII and hex.

If RPC works but the current car is missing, reproduce the issue and attach a reviewed `AzimuthRPC.log`. Inspect/redact personal information first: logs include module filenames, memory probe bytes and runtime telemetry. The most useful entry begins with `Car probe:`.

Only module file names are logged, not full module paths.

## FH2ML ABI note

`FH2ML_Initialize` receives the FH2ML host API pointer. The exact shared `sdk/FH2ML.h` is not vendored in this repository yet, so the current version treats that pointer as opaque instead of inventing an ABI layout. Once the real header is shared between the projects, host logging/registration can be wired without changing the runtime design.

## Build

From the repository root on Windows with Visual Studio Build Tools and CMake:

```powershell
./native/FH2ML.AzimuthRPC/build.ps1
```

Output:

```text
artifacts/FH2ML-AzimuthRPC-0.9.1/
  AzimuthRPC.dll
  mod.json
  README.md
  LICENSE
```

The build script reads the package version from `mod.json`, so future versions do not require a hardcoded output path change.

## Install

Copy the packaged folder as one manifest-owned mod, for example:

```text
Azimuth/
  mods/
    azimuth-rpc/
      AzimuthRPC.dll
      mod.json
      README.md
```

`mod.json` is enabled by default and currently uses priority `500` with no dependency or ordering edges.

## Rich Presence behavior

AzimuthRPC omits Discord's second `state` line when it has no useful value to show. For example, `Horizon Hub` is shown on its own if the car cannot currently be resolved instead of adding a redundant `Forza Horizon 2 via AzimuthRPC` line.

## Limitations

- FH2ML itself is still a development loader and its Azimuth compatibility matrix is not yet fully live-validated.
- This DLL uses conservative memory-only world-presence logic rather than the standalone debug host's external telemetry hooks.
- The current location names are macro regions inferred from player coordinates, not a native FH2 road/landmark source.
- Discord IPC is retried automatically if Discord is not running or restarts.
- No hot-unload support is attempted.
## FH2ML Host API integration

The native DLL is built against the vendored FH2ML SDK v1 header in `fh2ml-sdk/`.
During `FH2ML_Initialize` it validates the host table, registers `azimuth-rpc`,
mirrors diagnostics through the FH2ML logger, and obtains the mapped FH2 image
from `GetGameModule()`. It does not scan the process address space for the game.

The vendored FH2ML SDK header is licensed separately under MIT; see
`fh2ml-sdk/LICENSE`.

## Experimental current-car detection (Extra Options method)

This development-only build has an **opt-in, in-process** vehicle probe based on
[RokkuDayo's Extra Options](https://github.com/RokkuDayo/ForzaHorizon2_ExtraOptions)
`GetLocalPlayer -> GetPlayerCar` design. The known FH2 1.0.0.64 RVAs are:

- `PlayerManager` root: `0x34ABA60`
- `GetLocalPlayer`: `0x21E0C0`
- `GetPlayerCar`: `0x2DA060`
- expected car-object vtable: `0x2C5CB80`
- observed numeric model ID: `car + 0x118`

This uses **game functions** on AzimuthRPC's existing worker thread.
Their thread-safety has **not** been validated. The feature is disabled by
default and is **not a production car-detection fix**. Do not recommend it
to general users until live behavior has been verified. It does not patch
the Extra Options vehicle-update hook or any game code.

For developer-controlled testing only, set the following environment
variable *before launching Azimuth/FH2ML*:

```powershell
$env:AZIMUTH_RPC_EXPERIMENTAL_CAR_OBJECT = '1'
# Launch Azimuth from the same shell so the environment is inherited.
```

Unset it or set a different value to revert to the previous car reader.

**Fail-closed rules:** accept only the supported FH2 PE, validate the
player-car vtable and pointer, read the numeric ID with a guarded read,
release the owned car reference via the verified vtable method, and stop
probing for the session on unsafe release, unexpected vtable or a caught
access violation. No stale model name is recycled when the local player
has no car. A valid numeric ID can provide fallback presence
`Vehicle model #1234` if the AudioEngine text cannot be decoded.
When two consecutive samples report the same model ID and a valid
AudioEngine name, the matching friendly name is remembered in memory
for later failed string reads. This is **learned from runtime observation**,
not an authoritative numeric-ID-to-name database; it can be wrong if
the text field lags a vehicle change.

Watch the log for `Local car object: ok model-id=...` and compare it
with the actual selected car before and after several garage, free-roam,
Extra Options and race transitions. Report crashes, wrong car names,
unreleased references, or logs showing `fault`, `unsafe-release`,
`invalid-vtable`; leave the feature OFF if anything looks wrong.
The probe does not exist in the standalone C# EXE because the standalone
process cannot safely call these in-process game methods.

**Research to finish:** confirm on the game thread that the car getter
lifetime and field `car + 0x118` are valid, add a proper host game-thread
callback if FH2ML grows one, and validate a numeric-ID-to-`MediaName`
source. Until then, do not merge this branch into a production release.
