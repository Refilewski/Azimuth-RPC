# AzimuthRPC as an FH2ML native mod

This directory builds the native x64 `AzimuthRPC.dll` shipped with AzimuthRPC 0.9.1 and intended to be loaded by FH2ML as a manifest mod.

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

The native mod writes:

```text
<Azimuth directory>/AzimuthRPC.log
```

The file is capped at roughly 2 MiB and is truncated automatically once it grows beyond that size.

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
