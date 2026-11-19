#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/src/ScholarFlow.Desktop/ScholarFlow.Desktop.csproj"
DIST_DIR="$ROOT_DIR/dist"
VERSION="${1:-v1.0.0}"

rm -rf "$DIST_DIR/linux-x64" "$DIST_DIR/win-x64"
mkdir -p "$DIST_DIR/linux-x64" "$DIST_DIR/win-x64"

echo "==> Publishing ScholarFlow $VERSION for Linux (linux-x64)..."
dotnet publish "$PROJECT" \
  --disable-build-servers \
  -m:1 \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$DIST_DIR/linux-x64"

cp -f "$ROOT_DIR/assets/scholarflow.svg" "$DIST_DIR/linux-x64/scholarflow.svg"
cp -f "$ROOT_DIR/scripts/install-linux.sh" "$DIST_DIR/linux-x64/install.sh"
cp -f "$ROOT_DIR/scripts/uninstall-linux.sh" "$DIST_DIR/linux-x64/uninstall.sh"
chmod +x "$DIST_DIR/linux-x64/ScholarFlow" "$DIST_DIR/linux-x64/install.sh" "$DIST_DIR/linux-x64/uninstall.sh"

echo "==> Publishing ScholarFlow $VERSION for Windows (win-x64)..."
dotnet publish "$PROJECT" \
  --disable-build-servers \
  -m:1 \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$DIST_DIR/win-x64"

cp -f "$ROOT_DIR/scripts/install-windows.ps1" "$DIST_DIR/win-x64/install.ps1"
cp -f "$ROOT_DIR/scripts/uninstall-windows.ps1" "$DIST_DIR/win-x64/uninstall.ps1"

echo "==> Creating release archives..."
tar -C "$DIST_DIR/linux-x64" -czf "$DIST_DIR/ScholarFlow-$VERSION-linux-x64.tar.gz" \
  ScholarFlow scholarflow.svg install.sh uninstall.sh

python3 - "$DIST_DIR/win-x64" "$DIST_DIR/ScholarFlow-$VERSION-win-x64.zip" <<'PY'
import os, sys, zipfile
src_dir, out_zip = sys.argv[1], sys.argv[2]
with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED) as zf:
    for name in ("ScholarFlow.exe", "install.ps1", "uninstall.ps1"):
        p = os.path.join(src_dir, name)
        if os.path.exists(p):
            zf.write(p, arcname=name)
PY

echo "==> Done! Release artifacts:"
ls -lh "$DIST_DIR/ScholarFlow-$VERSION-linux-x64.tar.gz" "$DIST_DIR/ScholarFlow-$VERSION-win-x64.zip"
