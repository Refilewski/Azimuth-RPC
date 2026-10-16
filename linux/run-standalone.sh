#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 2 ]]; then
  echo "Usage: $0 <AzimuthRPC.DiscordBridge> <AzimuthRPC.exe> [wine-command]" >&2
  exit 2
fi

bridge="$1"
rpc_exe="$2"
wine_cmd="${3:-wine}"
port="${AZIMUTHRPC_BRIDGE_PORT:-64630}"

"$bridge" --port "$port" &
bridge_pid=$!
cleanup() {
  kill "$bridge_pid" 2>/dev/null || true
  wait "$bridge_pid" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

export AZIMUTHRPC_DISCORD_TCP="127.0.0.1:$port"
"$wine_cmd" "$rpc_exe"
