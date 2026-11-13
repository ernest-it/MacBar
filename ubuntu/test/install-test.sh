#!/bin/bash
# Installs the release zip with install.sh (as a user would) and checks GNOME Shell loads it cleanly.
UUID=macbar@ernest-it.github.io
export XDG_RUNTIME_DIR=/tmp/xdg; mkdir -p -m 700 $XDG_RUNTIME_DIR
cp -r /src/dist /tmp/rel
dbus-run-session -- bash -c "
  bash /tmp/rel/install.sh
  echo enabled: \$(gsettings get org.gnome.shell enabled-extensions)
  gnome-shell --headless --wayland --virtual-monitor 1600x900 --no-x11 > /tmp/shell.log 2>&1 &
  sleep 12
  gnome-extensions info $UUID | grep -E 'State|Version'
  grep -E 'JS ERROR|JS WARNING' /tmp/shell.log | head
  kill %1
"
