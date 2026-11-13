#!/bin/bash
# Removes MacBar for Ubuntu and turns Ubuntu Dock back on.
UUID=macbar@ernest-it.github.io
gnome-extensions disable "$UUID" 2>/dev/null || true
CUR=$(gsettings get org.gnome.shell enabled-extensions)
NEW=$(echo "$CUR" | sed "s/, '$UUID'//; s/'$UUID', //; s/'$UUID'//")
gsettings set org.gnome.shell enabled-extensions "$NEW"
rm -rf "$HOME/.local/share/gnome-shell/extensions/$UUID"
gnome-extensions enable ubuntu-dock@ubuntu.com 2>/dev/null || true
echo "MacBar is removed. Log out and back in to finish."
echo "(Your saved workspaces stay in ~/.config/macbar; delete that folder too if you like.)"
