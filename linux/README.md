# Experimental Linux / Wine support

AzimuthRPC can run under **Wine** while Discord runs natively on Linux.

Discord on Linux exposes a Unix-domain IPC socket, while AzimuthRPC runs inside Wine and normally expects a Windows Discord named pipe. This repository includes `AzimuthRPC.DiscordBridge`, a small native Linux helper that listens only on loopback TCP and forwards the raw Discord IPC stream to the local Discord socket.

## Current scope

Supported in this Linux/Wine pass:

- native Linux Discord desktop client;
- Azimuth / Forza Horizon 2 running through Wine;
- standalone `AzimuthRPC.exe` under Wine;
- the native FH2ML `AzimuthRPC.dll` loaded by Azimuth under Wine;
- Discord Rich Presence through the bundled loopback bridge;
- automatic Discord socket discovery in `XDG_RUNTIME_DIR`, `TMPDIR`, `TMP`, `TEMP`, then `/tmp`.

The FH2ML DLL keeps using normal Windows named-pipe Discord IPC on Windows. When `AZIMUTHRPC_DISCORD_TCP` is set, its Discord pipe calls are redirected to the loopback TCP bridge instead.

Not supported yet:

- a fully native Linux AzimuthRPC game-memory reader;
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

## Standalone EXE under Wine

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

If Azimuth uses a custom Wine prefix, export the same `WINEPREFIX` before launching AzimuthRPC.

## FH2ML DLL under Wine

Install the FH2ML mod normally inside your Azimuth directory, then launch Azimuth through the bridge helper:

```bash
bash linux/run-fh2ml.sh \
  artifacts/AzimuthRPC-DiscordBridge-linux-x64/AzimuthRPC.DiscordBridge \
  /path/to/Azimuth.exe
```

`run-fh2ml.sh` exports `AZIMUTHRPC_DISCORD_TCP` before starting Azimuth, so the environment variable is inherited by the process that loads `AzimuthRPC.dll`.

Do not run the standalone EXE and FH2ML DLL at the same time, because both can update the same Discord Rich Presence.

## Manual mode

You can also run the bridge yourself:

```bash
./AzimuthRPC.DiscordBridge --port 64630
```

Then launch either standalone AzimuthRPC or Azimuth/FH2ML from another terminal:

```bash
export AZIMUTHRPC_DISCORD_TCP=127.0.0.1:64630
wine /path/to/AzimuthRPC.exe
```

or:

```bash
export AZIMUTHRPC_DISCORD_TCP=127.0.0.1:64630
wine /path/to/Azimuth.exe
```

The bridge binds only to `127.0.0.1`. The Wine-side transport accepts only `127.0.0.1` or `localhost` endpoints.

When `AZIMUTHRPC_DISCORD_TCP` is not set, Windows builds continue to use normal Discord named-pipe IPC.
