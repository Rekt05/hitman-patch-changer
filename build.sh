#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
case "$(uname -m)" in
  x86_64|amd64) rid=linux-x64 ;;
  aarch64|arm64) rid=linux-arm64 ;;
  armv7l|armhf) rid=linux-arm ;;
  *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac
out="artifacts/$rid"
dotnet publish -c Release -r "$rid" --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$out"
chmod +x "$out/HitmanPatchChanger"
