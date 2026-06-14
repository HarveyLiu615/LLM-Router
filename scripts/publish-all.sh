#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/DesensitizeProxy.AspNetCore/DesensitizeProxy.AspNetCore.csproj"
OUT="$ROOT/artifacts/publish"
RIDS=(osx-arm64 osx-x64 linux-x64 linux-arm64 win-x64 win-arm64)

for rid in "${RIDS[@]}"; do
  dotnet publish "$PROJECT" \
    -c Release \
    -r "$rid" \
    --self-contained false \
    -o "$OUT/$rid"
done
