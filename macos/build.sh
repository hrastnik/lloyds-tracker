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

codesign --force --sign - "$APP"

echo ""
echo "✅ Gotovo: $APP"
echo ""
echo "Instalacija:  cp -r $APP /Applications/"
echo "Pokretanje:   open $APP"
