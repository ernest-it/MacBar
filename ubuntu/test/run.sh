#!/bin/bash
# Runs MacBar inside a headless GNOME Shell (Wayland, virtual 1920x1080 monitor) and its self-test.
# Usage (from the repo root on the host): docker run --rm -v "$PWD:/src" -v "$PWD/test/out:/out" macbar-gnome bash /src/test/run.sh
set -u
UUID=macbar@ernest-it.github.io
EXT=$HOME/.local/share/gnome-shell/extensions/$UUID
mkdir -p "$EXT" /out
rm -f /out/*.png /out/done /out/results.json /out/shell.log
cp -r /src/$UUID/. "$EXT/"
glib-compile-schemas "$EXT/schemas"
export XDG_RUNTIME_DIR=/tmp/xdg; mkdir -p -m 700 $XDG_RUNTIME_DIR
export MACBAR_SELFTEST=/out
dbus-run-session -- bash -c "
  gsettings set org.gnome.shell disable-user-extensions false
  gsettings set org.gnome.shell enabled-extensions \"['$UUID']\"
  gsettings set org.gnome.desktop.interface color-scheme prefer-dark
  gsettings set org.gnome.shell favorite-apps \"['org.gnome.Nautilus.desktop', 'org.gnome.TextEditor.desktop']\"
  pulseaudio --daemonize=yes --exit-idle-time=-1 >/dev/null 2>&1; sleep 1
  pactl load-module module-null-sink sink_name=speakers sink_properties=device.description=Speakers >/dev/null
  pactl load-module module-null-sink sink_name=headphones sink_properties=device.description=Headphones >/dev/null
  pactl load-module module-virtual-source source_name=mic master=speakers.monitor source_properties=device.description=USB-Microphone >/dev/null
  pactl set-default-source mic
  gnome-shell --headless --wayland --virtual-monitor 1920x1080 --no-x11 > /out/shell.log 2>&1 &
  SHELL_PID=\$!
  for i in \$(seq 1 240); do [ -f /out/done ] && break; sleep 1; done
  sleep 1
  kill \$SHELL_PID 2>/dev/null; sleep 1
"
echo "---- shell errors ----"
grep -iE "JS ERROR|JS WARNING|macbar|Extension.*error|TypeError|ReferenceError" /out/shell.log | grep -v MACBAR-TEST | head -60
echo "---- results ----"
grep MACBAR-TEST /out/shell.log
[ -f /out/results.json ] || echo "NO RESULTS (self-test did not finish)"
