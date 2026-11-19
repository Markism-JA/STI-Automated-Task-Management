#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/src/ScholarFlow.Desktop/ScholarFlow.Desktop.csproj"
DIST_DIR="$ROOT_DIR/dist"

echo "==> Publishing ScholarFlow for Linux (linux-x64)..."
dotnet publish "$PROJECT" \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$DIST_DIR/linux-x64"

echo "==> Publishing ScholarFlow for Windows (win-x64)..."
dotnet publish "$PROJECT" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$DIST_DIR/win-x64"

echo "==> Done! Outputs:"
ls -lh "$DIST_DIR/linux-x64/ScholarFlow" "$DIST_DIR/win-x64/ScholarFlow.exe"
