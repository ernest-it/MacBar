#!/bin/bash
# Installs dist/MacBar-Ubuntu.deb with apt while a user session is running (with the real Ubuntu Dock
# installed and on), checks that:
#   - installing doesn't turn Ubuntu Dock off yet (MacBar isn't running until the next sign-in)
#   - after "signing in" GNOME Shell loads MacBar from /usr/share and MacBar turns Ubuntu Dock off
#   - turning MacBar off gives Ubuntu Dock back; removing the package cleans up
# Run as root: docker run --rm --user root -v "$PWD:/src" macbar-gnome bash /src/test/deb-test.sh
UUID=macbar@ernest-it.github.io
DOCK=ubuntu-dock@ubuntu.com
apt-get update -qq >/dev/null && apt-get install -y -qq gnome-shell-extension-ubuntu-dock >/dev/null 2>&1 || echo "could not install Ubuntu Dock"
mkdir -p /run/dbus && dbus-daemon --system --fork
U=$(id -u tester); mkdir -p -m 700 /run/user/$U && chown tester /run/user/$U
su tester -c "export XDG_RUNTIME_DIR=/run/user/$U; dbus-daemon --session --address=unix:path=/run/user/$U/bus --fork --nopidfile"
as_user() { su tester -c "export XDG_RUNTIME_DIR=/run/user/$U DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$U/bus; $1"; }
lists() { as_user "echo '  enabled:' \$(gsettings get org.gnome.shell enabled-extensions); echo '  disabled:' \$(gsettings get org.gnome.shell disabled-extensions)"; }
# Ubuntu's session turns Ubuntu Dock on through its session mode; the plain GNOME session here needs the setting.
as_user "gsettings set org.gnome.shell enabled-extensions \"['$DOCK']\""

echo "== install"; apt-get install -y /src/dist/MacBar-Ubuntu.deb 2>&1 | grep -E "Setting up|MacBar|rror"
lists
echo "== sign in"
as_user "gnome-shell --headless --wayland --virtual-monitor 1600x900 --no-x11 > /tmp/shell.log 2>&1 &
  sleep 14
  echo -n '  MacBar: '; gnome-extensions info $UUID | grep -E 'State' | tr -d ' '
  gnome-extensions info $UUID | grep -E 'Path' | sed 's/^ */  /'
  echo -n '  Ubuntu Dock: '; gnome-extensions info $DOCK | grep -E 'State' | tr -d ' '
  echo schema: \$(gsettings get org.gnome.shell.extensions.macbar bar-opacity 2>&1)
  echo '== user turns MacBar off'
  gnome-extensions disable $UUID; sleep 3
  echo -n '  MacBar: '; gnome-extensions info $UUID | grep -E 'State' | tr -d ' '
  echo -n '  Ubuntu Dock: '; gnome-extensions info $DOCK | grep -E 'State' | tr -d ' '
  echo '== user turns MacBar back on'
  gnome-extensions enable $UUID; sleep 3
  echo -n '  MacBar: '; gnome-extensions info $UUID | grep -E 'State' | tr -d ' '
  echo -n '  Ubuntu Dock: '; gnome-extensions info $DOCK | grep -E 'State' | tr -d ' '
  pkill -u tester gnome-shell; sleep 1"
grep -nE "JS ERROR|JS WARNING|MacBar:|Extension|startup|dock" /tmp/shell.log | grep -v "^.*Gtk" | head -20; grep -n -A6 "JS ERROR" /tmp/shell.log | head -20
lists
echo "== autostart (fresh user)"
useradd -m second; su second -c "export XDG_RUNTIME_DIR=/tmp/x2; mkdir -p -m 700 /tmp/x2; dbus-run-session -- bash -c '/usr/libexec/macbar/macbar-setup --login 2>/dev/null; echo \"  enabled:\" \$(gsettings get org.gnome.shell enabled-extensions); gsettings set org.gnome.shell enabled-extensions \"@as []\"; /usr/libexec/macbar/macbar-setup --login; echo \"  after the user turned it off and signed in again:\" \$(gsettings get org.gnome.shell enabled-extensions)'" 2>&1 | grep -v dbus-daemon
echo "== remove"; apt-get remove -y macbar 2>&1 | grep -E "Removing|MacBar|rror"
lists
ls /usr/share/gnome-shell/extensions/$UUID 2>&1 | head -1
