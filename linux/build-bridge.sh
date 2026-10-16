#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="${1:-$repo_root/artifacts/AzimuthRPC-DiscordBridge-linux-x64}"

cd "$repo_root"
dotnet publish src/AzimuthRPC.DiscordBridge/AzimuthRPC.DiscordBridge.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  --nologo \
  -p:PublishSingleFile=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -o "$output"

echo "Linux Discord bridge published to: $output"
