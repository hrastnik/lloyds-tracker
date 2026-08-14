#!/usr/bin/env bash
# Builda release binary i pakira dist/lloyds-tracker-linux-x86_64.tar.gz
# (binary + .desktop + ikona + install.sh). Traži GTK4 dev pakete:
#
#   Debian/Ubuntu:  sudo apt install libgtk-4-dev build-essential
#   Fedora:         sudo dnf install gtk4-devel
#   Arch:           sudo pacman -S gtk4
set -euo pipefail
cd "$(dirname "$0")"

echo "→ cargo build --release"
cargo build --release

VERSION=$(grep -m1 '^version' Cargo.toml | cut -d'"' -f2)
ARCH=$(uname -m)
NAME="lloyds-tracker-linux-$ARCH"
STAGE="dist/$NAME"

rm -rf dist
mkdir -p "$STAGE"
install -m 755 target/release/lloyds-tracker "$STAGE/lloyds-tracker"
install -m 644 packaging/lloyds-tracker.desktop "$STAGE/lloyds-tracker.desktop"
install -m 644 packaging/lloyds-tracker.svg "$STAGE/lloyds-tracker.svg"
install -m 755 packaging/install.sh "$STAGE/install.sh"

tar -czf "dist/$NAME.tar.gz" -C dist "$NAME"

echo ""
echo "✅ Gotovo: dist/$NAME.tar.gz (v$VERSION, $(du -h "$STAGE/lloyds-tracker" | cut -f1))"
echo ""
echo "Instalacija:  tar -xzf dist/$NAME.tar.gz && $NAME/install.sh"
echo "Pokretanje:   lloyds-tracker"
