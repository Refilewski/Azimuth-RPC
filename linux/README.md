# Experimental Linux / Wine support

AzimuthRPC can run its **standalone Windows build under Wine** while Discord runs natively on Linux.

The Windows process cannot normally reach Discord's Linux Unix-domain IPC socket, so this repository includes `AzimuthRPC.DiscordBridge`: a small native Linux helper that listens only on loopback TCP and forwards the raw Discord IPC stream to the local Discord socket.

## Current scope

Supported in this first Linux pass:

- native Linux Discord desktop client;
- Azimuth / Forza Horizon 2 running through Wine;
- `AzimuthRPC.exe` running inside the same Wine environment as Azimuth;
- Discord Rich Presence through the bundled loopback bridge;
- automatic Discord socket discovery in `XDG_RUNTIME_DIR`, `TMPDIR`, `TMP`, `TEMP`, then `/tmp`.

Not supported yet:

- a fully native Linux AzimuthRPC game-memory reader;
- the FH2ML DLL using the bundled TCP bridge directly;
- Flatpak-specific Discord socket remapping when the socket is hidden from the host process.

## Build the bridge

Requires the .NET 8 SDK on Linux.

```bash
bash linux/build-bridge.sh
```

Default output:

```text
artifacts/AzimuthRPC-DiscordBridge-linux-x64/
```

## Run

Make sure native Discord is open first. Then run:

```bash
bash linux/run-standalone.sh \
  artifacts/AzimuthRPC-DiscordBridge-linux-x64/AzimuthRPC.DiscordBridge \
  /path/to/AzimuthRPC.exe
```

The launcher starts the bridge and passes this environment variable to Wine:

```text
AZIMUTHRPC_DISCORD_TCP=127.0.0.1:64630
```

AzimuthRPC uses normal Windows named-pipe Discord IPC when this variable is not set, so the Windows path remains unchanged.

If Azimuth uses a custom Wine prefix, export the same `WINEPREFIX` before launching AzimuthRPC.

## Manual mode

You can also run both pieces yourself:

```bash
./AzimuthRPC.DiscordBridge --port 64630
```

Then in another terminal:

```bash
export AZIMUTHRPC_DISCORD_TCP=127.0.0.1:64630
wine /path/to/AzimuthRPC.exe
```

The bridge binds only to the local loopback interface. AzimuthRPC also rejects non-loopback values in `AZIMUTHRPC_DISCORD_TCP`.
