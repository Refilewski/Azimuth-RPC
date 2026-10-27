# AzimuthRPC — FH2 Mod Platform

Native Windows x64 Discord Rich Presence plugin for Trinculo54's FH2 Mod Platform 2.7.3+. It exports `fh2mod_init`, registers itself with the platform and uses platform-provided logging and the mapped FH2 base address. It is independent of the .NET runtime and does not patch gameplay.

## Build and install

Build on Windows x64 with Visual Studio Build Tools and CMake:

```powershell
./native/FH2ML.AzimuthRPC/build.ps1
```

The script downloads the official FH2 Mod Platform 2.7.3 distribution for packaging. Extract the contents of `FH2ModPlatform-AzimuthRPC-<version>` next to `azimuth.exe`:

```text
Azimuth/
  azimuth.exe
  d3dcompiler_47.dll
  fh2-mods/
    AzimuthRPC.dll
```

Keep a newer official `d3dcompiler_47.dll` if another mod already installed it. The included `LICENSE`, `LICENSE-FH2ModPlatform.txt` and `LICENSE-MinHook.txt` are important redistributable notices. `mod.json` is internal version metadata only; it is not shipped or consumed by FH2 Mod Platform.

## Operation and diagnostics

The platform loads the plugin before the game is mapped. The AzimuthRPC worker waits for a validated FH2 1.0.0.64 image, then polls car, speed, approximate mainland location and game activity and updates Discord IPC.

- Native log: `%LOCALAPPDATA%\\AzimuthRPC\\logs\\AzimuthRPC.log`
- Platform load/registration log: `fh2-mods\\platform.log`
- If the known `AudioEngine+0x278` car ID cannot be parsed, the plugin tests a bounded region of the same object for a **unique** plausible identifier. The log reports `fallback@0x...` when recovery succeeds. Two different plausible IDs cause the fallback to reject the sample rather than guess.

The same guarded fallback exists in standalone EXE. **No live test for Azimuth 1.0.2 is available yet**: neither a new offset nor a guaranteed fix is claimed. Reproduce in a car and check the logged probe status. Review/redact logs before sharing, because they may include module names and telemetry.

Do not run EXE and DLL simultaneously. Hot unload is not implemented.
