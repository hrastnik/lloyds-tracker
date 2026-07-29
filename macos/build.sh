#!/bin/zsh
# Builda LloydsTracker.app u dist/ folderu.
set -e
cd "$(dirname "$0")"

echo "→ swift build -c release"
swift build -c release

APP="dist/LloydsTracker.app"
BIN=".build/release/LloydsTracker"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/LloydsTracker"
cp Support/Info.plist "$APP/Contents/Info.plist"

# Ikona se crta iz istog koda kao pločica u traci (AppIcon.swift), pa ne mogu razići.
echo "→ AppIcon.icns"
swiftc -O Support/IconGen/main.swift \
  Sources/LloydsTracker/AppIcon.swift \
  Sources/LloydsTracker/Theme.swift \
  -o .build/icongen
.build/icongen "$APP/Contents/Resources/AppIcon.icns"

codesign --force --sign - "$APP"

echo ""
echo "✅ Gotovo: $APP"
echo ""
echo "Instalacija:  cp -r $APP /Applications/"
echo "Pokretanje:   open $APP"
