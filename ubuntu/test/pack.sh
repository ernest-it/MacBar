#!/bin/bash
# Builds the release zip with GNOME's own packer (validates metadata and compiles schemas).
set -e
UUID=macbar@ernest-it.github.io
mkdir -p /src/dist
cd /src/$UUID
gnome-extensions pack --force --extra-source=lib --schema=schemas/org.gnome.shell.extensions.macbar.gschema.xml -o /src/dist .
cp /src/install.sh /src/uninstall.sh /src/dist/
ls -la /src/dist
unzip -l /src/dist/$UUID.shell-extension.zip
