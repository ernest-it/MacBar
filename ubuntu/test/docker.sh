#!/bin/bash
# Container entry point: a system D-Bus (GNOME Shell needs one), then the test as a normal user.
mkdir -p /run/dbus && dbus-daemon --system --fork
mkdir -p /out && chown tester /out
su tester -c "bash /src/test/run.sh"
