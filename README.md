# AzimuthRPC

Author and maintainer: **Refilewski**.

Discord Rich Presence for Forza Horizon 2 running through Azimuth, with both a standalone Windows host and a native FH2ML mod.

**0.9.1 — dual-runtime release line.** The project ships two supported build targets from the same repository:

- `AzimuthRPC.exe` — standalone Windows x64 tray application.
- `AzimuthRPC.dll` — native x64 FH2ML mod, also packaged with `mod.json` and its README.

## Supported setup

AzimuthRPC targets **Forza Horizon 2 Xbox One 1.0.0.64** on Windows x64 through Azimuth. The supported image identity is PE timestamp `565501ED`, image size `3A18000`, entry RVA `F18058`.

Unsupported game builds fail closed instead of applying the known offsets to an unknown layout.

**No game files or raw memory captures are included in this tree.** Users need their own copy of Forza Horizon 2. Tests construct minimal synthetic memory; they do not ship game assets.

The development setup uses Azimuth on Windows x64. The FH2 image check does not certify every Azimuth version or mod combination. Report the exact Azimuth/FH2ML versions when testing a different setup.

## What it detects

Current validated/runtime support includes:

- mapped FH2 build and GameBase discovery;
- current car ID/name;
- speed and live XYZ/velocity data;
- Pause, Photo Mode, World Map, Horizon Hub, Garage and Loading states;
- Discord Rich Presence over Discord's local named-pipe IPC;
- mainland macro-location resolution used by presence.

The current location resolver is based on live player coordinates plus validated anchors/coarse polygons. It is **not** a discovered native FH2 road/region API. Native road/landmark/location research remains separate under `docs/research/`.

## Standalone EXE

The standalone build is a tray-only RPC host. Start Discord desktop, launch Azimuth/FH2, then run `AzimuthRPC.exe`. It waits for the supported game image automatically. Use the tray menu to exit. Run one RPC runtime at a time to avoid duplicate presence updates. The developer WPF/research UI is a separate build target.

Build it with:

```powershell
./publish.ps1
```

Output:

```text
artifacts/AzimuthRPC-0.9.1-win-x64/AzimuthRPC.exe
```

## FH2ML native mod

`native/FH2ML.AzimuthRPC` is a separate native C++ implementation intended for FH2ML. It does **not** host the .NET runtime inside Azimuth.

The DLL currently:

- exports `FH2ML_Initialize`;
- validates the supported FH2 image;
- performs guarded direct in-process reads for the current car, speed and position;
- reproduces the current memory-based UI/state detection;
- resolves the current mainland macro location from player coordinates;
- talks directly to Discord IPC;
- writes `AzimuthRPC.log` beside the Azimuth executable for public diagnostics.

For car-detection reports, the native log records the `AudioEngine + 0x278` parse result plus 64 raw bytes as ASCII/hex when that field changes. It also lists loaded module file names so compatibility reports can show setups such as Extra Options or radio mods without exposing full paths.

When there is no useful second Rich Presence line to show, the native mod now omits Discord's `state` field instead of displaying a redundant fallback such as `Forza Horizon 2 via AzimuthRPC`.

Build it with:

```powershell
./native/FH2ML.AzimuthRPC/build.ps1
```

Package layout:

```text
artifacts/FH2ML-AzimuthRPC-0.9.1/
  AzimuthRPC.dll
  mod.json
  README.md
```

Install the package as one FH2ML manifest mod, for example:

```text
Azimuth/
  mods/
    azimuth-rpc/
      AzimuthRPC.dll
      mod.json
      README.md
```

The native MVP does not install gameplay patches or the standalone debug host's external telemetry hooks. Event-vs-free-roam telemetry and native road/region discovery are still follow-up work.

## Build and test

Windows x64 with the .NET 8 SDK/Windows Desktop targeting pack is required for the managed solution. Building the native mod additionally requires Visual Studio Build Tools and CMake.

Run the full managed validation and smoke test with:

```powershell
./build.ps1 -SmokeTest
```

The regression suites are executable assertion projects rather than a VSTest adapter:

```powershell
dotnet build AzimuthRPC.sln -c Release
dotnet run --project tests/FH2UiMode.Tests -c Release --no-build
dotnet run --project tests/AzimuthRPC.Core.Tests -c Release --no-build
```

## Repository layout

```text
src/AzimuthRPC.App/            public standalone tray app
src/AzimuthRPC.Core/           shared managed snapshot/state/presence logic
src/AzimuthRPC.Debug/          developer WPF + reverse-engineering tools
src/AzimuthRPC.InProcess/      managed guarded-read adapter experiments
native/FH2ML.AzimuthRPC/       native FH2ML Discord RPC mod
tests/                         regression suites and committed fixtures
docs/                          architecture, offsets, provenance and research
```

Live research dumps remain local/ignored. Only purpose-built synthetic regression data belongs in `tests/fixtures/`; see [fixture policy](tests/fixtures/README.md).

## Release assets and diagnostics

The release pipeline builds `AzimuthRPC.exe`, `AzimuthRPC.dll` and
`FH2ML-AzimuthRPC-<version>.zip`. Version 0.9.1 in the source does not imply that
a 0.9.1 GitHub Release has been published. This cleanup does not publish one.

The native runtime writes `AzimuthRPC.log` beside Azimuth. It can contain runtime
telemetry, memory probe values (including car-field bytes), loaded module
filenames and Discord IPC state. **Inspect/redact logs before uploading them
publicly.** Module filename logging does not include full module paths, but other
diagnostics may still reveal personal context. Standalone failures can also be
investigated with the separate debug host; not every native diagnostic is emitted
by the standalone EXE.

For a missing car or other bug, use the issue form and include Azimuth version,
FH2 build, AzimuthRPC version, EXE/DLL choice, FH2ML version, Extra Options version,
other mods, exact car, where in game it happens and a reviewed `AzimuthRPC.log`
when the native runtime is used. Never attach raw memory dumps or game files.

## Research status

The project keeps reverse-engineering tooling because several useful FH2 signals are still being validated. In particular, a real native road/region/landmark source has not yet been promoted to production. See [MAP-RESEARCH](docs/research/MAP-RESEARCH.md), [OFFSETS](docs/OFFSETS.md) and [RESEARCH](docs/RESEARCH.md).

## Contribution and license status

See [CONTRIBUTING](CONTRIBUTING.md) for build/test requirements and data policy.
The [AzimuthRPC Use-Only License 1.0](LICENSE) permits inspection, installation,
local compilation and use, with the copies/backups needed for those activities.
Reusing code in another project and redistributing code or binaries require
separate written permission. GitHub hosting permissions and mandatory legal
exceptions remain unaffected. This is source-available software, not open source.
The repository remains private pending history migration and maintainer review.
The license does not override third-party rights; research provenance is recorded
in [PROVENANCE](docs/PROVENANCE.md).
The manual history/visibility procedure is in [PUBLIC_RELEASE](docs/PUBLIC_RELEASE.md).


