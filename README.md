<p align="center">
  <img src="src/AzimuthRPC.Debug/Assets/AzimuthRPC.ico" alt="AzimuthRPC" width="128" />
</p>

<h1 align="center">AzimuthRPC</h1>

<p align="center">
  Discord Rich Presence for <strong>Forza Horizon 2 on Azimuth</strong>.<br />
  Show your current car, speed, approximate location and in-game activity directly on Discord.
</p>

<p align="center">
  <a href="https://github.com/Refilewski/Azimuth-RPC/releases/latest"><img src="https://img.shields.io/badge/Download-Latest%20release-5865F2?style=for-the-badge&logo=github" alt="Download latest release" /></a>
  <a href="https://github.com/Refilewski/Azimuth-RPC/issues"><img src="https://img.shields.io/badge/Report-Issue-d73a4a?style=for-the-badge&logo=github" alt="Report an issue" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-Source--available-2ea44f?style=for-the-badge" alt="Source-available license" /></a>
</p>

## Features

- Live Discord Rich Presence for Forza Horizon 2 through Azimuth.
- Current car detection with friendly vehicle names.
- Live speed display.
- Approximate mainland location from player position.
- Detection for Pause Menu, Photo Mode, World Map, Horizon Hub, Garage and Loading states.
- Two ways to run it: a standalone Windows tray app or a native FH2ML mod.
- Experimental Linux/Wine support for the standalone app through a bundled Discord IPC bridge.
- Automatic Discord reconnect and presence refresh.
- Native diagnostic logging for compatibility and car-detection reports.
- Source available for inspection, local builds and development under the project license.

## Download & install

Requires **Discord desktop**, Azimuth and your own copy of Forza Horizon 2. The regular release path targets **Windows x64**.

Download the newest build from [GitHub Releases](https://github.com/Refilewski/Azimuth-RPC/releases/latest).

### Standalone

1. Download the Windows standalone package from the latest release.
2. Start Discord desktop.
3. Launch Azimuth and Forza Horizon 2.
4. Run `AzimuthRPC.exe`.
5. AzimuthRPC stays in the system tray and connects automatically when the supported FH2 build is detected.

### FH2ML mod

Download `FH2ML-AzimuthRPC-<version>.zip` from the latest release and extract it as one FH2ML mod:

```text
Azimuth/
  mods/
    azimuth-rpc/
      AzimuthRPC.dll
      mod.json
      README.md
```

The native mod runs inside Azimuth/FH2ML and talks directly to Discord IPC. Do not run the standalone EXE and FH2ML DLL at the same time, or both may try to update the same Rich Presence.

### Linux / Wine

Experimental Linux support is available for the **standalone EXE under Wine** while Discord runs natively on Linux. The bundled `AzimuthRPC.DiscordBridge` forwards Discord IPC over a loopback-only connection between Wine and Discord's Unix-domain socket.

See [linux/README.md](linux/README.md) for build and launch instructions.

## Compatibility

AzimuthRPC currently targets **Forza Horizon 2 Xbox One 1.0.0.64** through Azimuth. Unknown game builds are rejected instead of using offsets against an unsupported executable.

Compatibility can vary with Azimuth and FH2ML versions or other mods. If something breaks, include your exact setup in the issue report.

Linux/Wine support is currently experimental and covers the standalone runtime. Direct bundled-bridge support for the FH2ML DLL is still follow-up work.

## Diagnostics & privacy

The FH2ML version writes `AzimuthRPC.log` beside the Azimuth executable. It can contain runtime telemetry, memory probe values, loaded module names and Discord IPC status, so **review the log before uploading it publicly**.

AzimuthRPC does not ask for your Discord password or token. No game files, saves or raw memory captures are included in this repository.

## Build from source

The managed projects require the **.NET 8 SDK** on Windows x64. Building the native FH2ML DLL additionally requires **Visual Studio Build Tools** and **CMake**.

Standalone build:

```powershell
./publish.ps1
```

Native FH2ML build:

```powershell
./native/FH2ML.AzimuthRPC/build.ps1
```

Linux Discord bridge:

```bash
bash linux/build-bridge.sh
```

Full validation and smoke test:

```powershell
./build.ps1 -SmokeTest
```

More technical information is available in [`docs/`](docs/), including architecture, offsets and reverse-engineering research. Contribution requirements are documented in [CONTRIBUTING.md](CONTRIBUTING.md).

## Reporting issues

When reporting a problem, include:

- Azimuth version;
- FH2ML version if used;
- AzimuthRPC version and whether you use the EXE or DLL;
- operating system and Wine version when testing Linux;
- other FH2 mods such as Extra Options;
- the exact car or game state where the problem occurs;
- a reviewed `AzimuthRPC.log` when relevant.

Do not upload game files or raw memory dumps.

## License

AzimuthRPC is **source-available**, not OSI open source. The [AzimuthRPC Use-Only License 1.0](LICENSE) allows inspection, local compilation and personal use. Reuse of the code in another project or redistribution requires separate permission.

## Disclaimer

AzimuthRPC is an independent fan project by **Refilewski**. It is not affiliated with Microsoft, Xbox, Playground Games, Turn 10, Discord or Azimuth. No game, DLC, game files or license keys are included.
