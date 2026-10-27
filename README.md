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
- Automatic Discord reconnect and presence refresh.
- Native diagnostic logging for compatibility and car-detection reports.
- Source available for inspection, local builds and development under the project license.

## Download & install

Requires **Discord desktop**, **Windows x64**, Azimuth and your own copy of Forza Horizon 2.

Download the newest build from [GitHub Releases](https://github.com/Refilewski/Azimuth-RPC/releases/latest).

### Standalone

1. Download the Windows standalone package from the latest release.
2. Start Discord desktop.
3. Launch Azimuth and Forza Horizon 2.
4. Run `AzimuthRPC.exe`.
5. AzimuthRPC stays in the system tray and connects automatically when the supported FH2 build is detected.

### FH2 Mod Platform mod

Download `FH2ModPlatform-AzimuthRPC-<version>.zip` from the latest release and extract its contents next to `azimuth.exe`:

```text
Azimuth/
  azimuth.exe
  d3dcompiler_47.dll
  fh2-mods/
    AzimuthRPC.dll
```

The native DLL uses Trinculo54's **FH2 Mod Platform 2.7.3+**, not the earlier FH2ML manifest ABI. The zip ships the official platform loader. If other mods already installed a newer `d3dcompiler_47.dll`, keep the newer version.

The plugin runs in-process, using the platform's game-base service and local Discord IPC. **Do not run the standalone EXE and native DLL together**, because both may update the same Rich Presence.

## Compatibility

AzimuthRPC currently targets **Forza Horizon 2 Xbox One 1.0.0.64** on Windows x64 through Azimuth. Unknown game builds are rejected instead of using offsets against an unsupported executable.

Compatibility can vary with Azimuth and FH2 Mod Platform versions or other mods. If something breaks, include your exact setup in the issue report.

**Linux / Wine support is not available yet.** A Linux Discord IPC path/bridge is being explored.

## Diagnostics & privacy

Both the standalone EXE and the FH2ML DLL write `%LOCALAPPDATA%\\AzimuthRPC\\logs\\AzimuthRPC.log` (paste that path into Windows Explorer). The native DLL now logs as soon as `fh2mod_init` is called, including rejected host API versions or unsupported game-image checks. If no log is created, first verify the executable actually started or the loader called the DLL initializer; an absent log alone does **not** prove that car offsets changed. Logs can contain runtime telemetry, memory probe values, loaded module names and Discord IPC status, so **review the log before uploading it publicly**.

The **standalone EXE automatically removes `AzimuthRPC.log` and its rotated `.1` log after a normal exit** (tray `Exit`). If it crashes or is force-terminated, logs are kept for troubleshooting. To preserve logs while testing, launch the standalone with `--keep-logs`; copy the log before exiting if you need to share it. Cleanup is best-effort if another process keeps the file open.

The FH2 Mod Platform native DLL still retains logs: it has no safe, guaranteed shutdown callback, and attempting deletion inside `DllMain` during process teardown could cause instability. Don't run the standalone and native DLL at the same time.

### Share diagnostics on Discord (standalone)

Right-click the tray icon and choose **Send logs to Discord...**. A preview shows the exact content and nothing is uploaded until the user clicks Send. The EXE includes only recent diagnostic lines, with common Windows user profile names, credentials, and webhook URLs redacted. Please inspect the preview yourself before sharing.

The app contacts an HTTPS, rate-limited Supabase Edge Function owned by the FH2Mods project. It forwards a text attachment to the Discord support webhook, which is stored in Supabase Vault, **not embedded in the EXE or in GitHub**. This action is optional and does not run at startup or shutdown. The native FH2 Mod Platform DLL has no tray menu; its logs can still be shared manually.


AzimuthRPC does not ask for your Discord password or token. No game files, saves or raw memory captures are included in this repository.

## Build from source

The managed projects require the **.NET 8 SDK** on Windows x64. Building the native FH2 Mod Platform DLL additionally requires **Visual Studio Build Tools** and **CMake**.

Standalone build:

```powershell
./publish.ps1
```

Native FH2 Mod Platform build:

```powershell
./native/FH2ML.AzimuthRPC/build.ps1
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
- other FH2 mods such as Extra Options;
- the exact car or game state where the problem occurs;
- a reviewed `AzimuthRPC.log` when relevant.

Do not upload game files or raw memory dumps.

## License

AzimuthRPC is **source-available**, not OSI open source. The [AzimuthRPC Use-Only License 1.0](LICENSE) allows inspection, local compilation and personal use. Reuse of the code in another project or redistribution requires separate permission.

## Disclaimer

AzimuthRPC is an independent fan project by **Refilewski**. It is not affiliated with Microsoft, Xbox, Playground Games, Turn 10, Discord or Azimuth. No game, DLC, game files or license keys are included.

Compatibility note: both runtimes now attempt a bounded, ambiguity-checked fallback if the known car ID field cannot be parsed. This is a hypothesis for Azimuth 1.0.2, **not** an in-game verified fix; see `native/FH2ML.AzimuthRPC/README.md` for diagnostics.
