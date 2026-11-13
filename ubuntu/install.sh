#!/bin/bash
# Installs MacBar for Ubuntu (a GNOME Shell extension) for the current user. No sudo needed.
#   curl -fsSL https://github.com/ernest-it/MacBar/releases/latest/download/install.sh | bash
set -euo pipefail
UUID=macbar@ernest-it.github.io
ZIP_URL=https://github.com/ernest-it/MacBar/releases/latest/download/$UUID.shell-extension.zip
DEST=$HOME/.local/share/gnome-shell/extensions/$UUID

if ! command -v gnome-shell >/dev/null; then
  echo "MacBar needs GNOME Shell (the standard Ubuntu desktop)." >&2; exit 1
fi
VER=$(gnome-shell --version | grep -oE '[0-9]+' | head -1)
if [ "$VER" -lt 45 ]; then
  echo "MacBar needs GNOME 45 or newer (Ubuntu 24.04 or later). This is GNOME $VER." >&2; exit 1
fi

HERE=$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" 2>/dev/null && pwd || echo .)
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
if [ -f "$HERE/$UUID.shell-extension.zip" ]; then
  ZIP="$HERE/$UUID.shell-extension.zip"
elif [ -f "$HERE/$UUID/metadata.json" ]; then
  # Running from a clone of the repository.
  ZIP=""
else
  echo "Downloading MacBar…"
  ZIP="$TMP/$UUID.zip"
  if command -v curl >/dev/null; then curl -fsSL -o "$ZIP" "$ZIP_URL"; else wget -qO "$ZIP" "$ZIP_URL"; fi
fi

rm -rf "$DEST"; mkdir -p "$DEST"
if [ -n "$ZIP" ]; then
  if command -v unzip >/dev/null; then unzip -qo "$ZIP" -d "$DEST"
  else python3 -c "import zipfile,sys; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])" "$ZIP" "$DEST"; fi
else
  cp -r "$HERE/$UUID/." "$DEST/"
  rm -f "$DEST/selftest.js"
fi
glib-compile-schemas "$DEST/schemas"

# Ubuntu Dock (or Dash to Dock) would sit under MacBar's dock; turn it off. uninstall.sh turns it back on.
for other in ubuntu-dock@ubuntu.com dash-to-dock@micxgx.gmail.com; do
  gnome-extensions disable "$other" 2>/dev/null || true
done

# Enable MacBar. The running shell only discovers new extensions at the next sign-in, so write the setting directly.
gsettings set org.gnome.shell disable-user-extensions false
CUR=$(gsettings get org.gnome.shell enabled-extensions)
if [[ "$CUR" != *"$UUID"* ]]; then
  if [[ "$CUR" == "@as []" || "$CUR" == "[]" ]]; then NEW="['$UUID']"; else NEW="${CUR%]}, '$UUID']"; fi
  gsettings set org.gnome.shell enabled-extensions "$NEW"
fi
gnome-extensions enable "$UUID" 2>/dev/null || true

echo
echo "MacBar is installed."
if [ "${XDG_SESSION_TYPE:-}" = "x11" ]; then
  echo "Press Alt+F2, type r and press Enter to load it (or log out and back in)."
else
  echo "Log out and back in to start it."
fi
echo "Settings: click the logo at the top left → MacBar Settings…"
