#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIST="$SCRIPT_DIR/../dist/linux-x64"

if [ -f "$SCRIPT_DIR/ScholarFlow" ]; then
  BIN_SRC="$SCRIPT_DIR/ScholarFlow"
  ICON_SRC="$SCRIPT_DIR/scholarflow.svg"
elif [ -f "$REPO_DIST/ScholarFlow" ]; then
  BIN_SRC="$REPO_DIST/ScholarFlow"
  ICON_SRC="$SCRIPT_DIR/../assets/scholarflow.svg"
else
  echo "Error: ScholarFlow binary not found. Run ./scripts/publish.sh first or run from the release archive." >&2
  exit 1
fi

BIN_DIR="$HOME/.local/bin"
ICON_DIR="$HOME/.local/share/icons/hicolor/scalable/apps"
APP_DIR="$HOME/.local/share/applications"

mkdir -p "$BIN_DIR" "$ICON_DIR" "$APP_DIR"

cp -f "$BIN_SRC" "$BIN_DIR/ScholarFlow"
chmod +x "$BIN_DIR/ScholarFlow"

if [ -f "$ICON_SRC" ]; then
  cp -f "$ICON_SRC" "$ICON_DIR/scholarflow.svg"
fi

cat > "$APP_DIR/scholarflow.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=ScholarFlow
GenericName=Academic Task & Study Manager
Comment=Todoist + Obsidian academic basin, STI eLMS sync, Master PDF compiler, and Evidence-Grounded Quiz Studio
Exec=$BIN_DIR/ScholarFlow %U
Icon=scholarflow
Terminal=false
Categories=Education;Office;Utility;
Keywords=tasks;todoist;obsidian;pdf;quiz;study;elms;
StartupNotify=true
StartupWMClass=ScholarFlow
EOF

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APP_DIR" >/dev/null 2>&1 || true
fi

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -f -t "$HOME/.local/share/icons/hicolor" >/dev/null 2>&1 || true
fi

echo "Installed ScholarFlow:"
echo "  Binary : $BIN_DIR/ScholarFlow"
echo "  Icon   : $ICON_DIR/scholarflow.svg"
echo "  Desktop: $APP_DIR/scholarflow.desktop"
