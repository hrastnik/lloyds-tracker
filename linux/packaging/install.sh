#!/usr/bin/env sh
# Instalacija u home korisnika — bez roota, bez paketa. Pokreni iz raspakiranog arhiva:
#
#   ./install.sh              instalira u ~/.local
#   ./install.sh --uninstall  briše sve što je instalirao
#
set -eu

DIR=$(cd "$(dirname "$0")" && pwd)
BIN_DIR="${XDG_BIN_HOME:-$HOME/.local/bin}"
APPS_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
ICON_DIR="${XDG_DATA_HOME:-$HOME/.local/share}/icons/hicolor/scalable/apps"

BIN="$BIN_DIR/lloyds-tracker"
DESKTOP="$APPS_DIR/lloyds-tracker.desktop"
ICON="$ICON_DIR/lloyds-tracker.svg"
AUTOSTART="${XDG_CONFIG_HOME:-$HOME/.config}/autostart/lloyds-tracker.desktop"

if [ "${1:-}" = "--uninstall" ]; then
    rm -f "$BIN" "$DESKTOP" "$ICON" "$AUTOSTART"
    echo "Obrisano. Podaci u ${XDG_DATA_HOME:-$HOME/.local/share}/LloydsTracker/ su ostali."
    exit 0
fi

mkdir -p "$BIN_DIR" "$APPS_DIR" "$ICON_DIR"
install -m 755 "$DIR/lloyds-tracker" "$BIN"
install -m 644 "$DIR/lloyds-tracker.svg" "$ICON"

# Exec pokazuje na apsolutnu putanju — tako pokretač radi i kad ~/.local/bin nije u PATH-u.
sed "s|^Exec=.*|Exec=$BIN|" "$DIR/lloyds-tracker.desktop" > "$DESKTOP"
chmod 644 "$DESKTOP"

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS_DIR" || true

echo "Instalirano: $BIN"
case ":$PATH:" in
    *":$BIN_DIR:"*) echo "Pokreni s: lloyds-tracker" ;;
    *) echo "Napomena: $BIN_DIR nije u PATH-u — pokreni s: $BIN" ;;
esac
echo "Pokretanje kod prijave uključi u Postavke → Sustav."
