#!/bin/bash
# Installs dist/MacBar-Ubuntu.deb with apt while a user session is running, checks GNOME Shell loads
# MacBar from /usr/share, then removes the package. Run as root: docker run --rm --user root -v "$PWD:/src" macbar-gnome bash /src/test/deb-test.sh
UUID=macbar@ernest-it.github.io
mkdir -p /run/dbus && dbus-daemon --system --fork
U=$(id -u tester); mkdir -p -m 700 /run/user/$U && chown tester /run/user/$U
su tester -c "export XDG_RUNTIME_DIR=/run/user/$U; dbus-daemon --session --address=unix:path=/run/user/$U/bus --fork --nopidfile"
as_user() { su tester -c "export XDG_RUNTIME_DIR=/run/user/$U DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/$U/bus; $1"; }
echo "== install"; apt-get install -y /src/dist/MacBar-Ubuntu.deb 2>&1 | grep -E "Setting up|MacBar|rror"
as_user "echo enabled: \$(gsettings get org.gnome.shell enabled-extensions); echo disabled: \$(gsettings get org.gnome.shell disabled-extensions)"
echo "== shell"
as_user "gnome-shell --headless --wayland --virtual-monitor 1600x900 --no-x11 > /tmp/shell.log 2>&1 & sleep 14; gnome-extensions info $UUID | grep -E 'Path|State|Version'; echo schema: \$(gsettings get org.gnome.shell.extensions.macbar bar-opacity 2>&1); kill %1; sleep 1"
grep -E 'JS ERROR|JS WARNING' /tmp/shell.log | head
echo "== autostart (fresh user)"
useradd -m second; su second -c "export XDG_RUNTIME_DIR=/tmp/x2; mkdir -p -m 700 /tmp/x2; dbus-run-session -- bash -c '/usr/libexec/macbar/macbar-setup --login; echo enabled: \$(gsettings get org.gnome.shell enabled-extensions); gsettings set org.gnome.shell enabled-extensions \"@as []\"; /usr/libexec/macbar/macbar-setup --login; echo after-user-turned-off: \$(gsettings get org.gnome.shell enabled-extensions)'"
echo "== remove"; apt-get remove -y macbar 2>&1 | grep -E "Removing|MacBar|rror"
as_user "echo enabled: \$(gsettings get org.gnome.shell enabled-extensions); echo disabled: \$(gsettings get org.gnome.shell disabled-extensions)"
ls /usr/share/gnome-shell/extensions/$UUID 2>&1 | head -1
