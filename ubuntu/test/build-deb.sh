#!/bin/bash
# Builds dist/MacBar-Ubuntu.deb. Run in the test image: docker run --rm --user root -v "$PWD:/src" macbar-gnome bash /src/test/build-deb.sh
set -e
UUID=macbar@ernest-it.github.io
VERSION=$(grep -oP '"version-name":\s*"\K[^"]+' /src/$UUID/metadata.json)
R=$(mktemp -d)/macbar
EXT=$R/usr/share/gnome-shell/extensions/$UUID
mkdir -p "$EXT" $R/usr/share/glib-2.0/schemas $R/usr/libexec/macbar $R/etc/xdg/autostart $R/DEBIAN
cp -r /src/$UUID/. "$EXT/"
rm -rf "$EXT/selftest.js" "$EXT/schemas"
cp /src/$UUID/schemas/*.gschema.xml $R/usr/share/glib-2.0/schemas/
glib-compile-schemas --strict --dry-run /src/$UUID/schemas
install -m755 /src/packaging/macbar-setup $R/usr/libexec/macbar/
install -m644 /src/packaging/macbar-setup.desktop $R/etc/xdg/autostart/
install -m755 /src/packaging/postinst /src/packaging/prerm $R/DEBIAN/
sed "s/@VERSION@/$VERSION/" /src/packaging/control > $R/DEBIAN/control
echo "Installed-Size: $(du -sk --exclude=DEBIAN $R | cut -f1)" >> $R/DEBIAN/control
find $R -type d -exec chmod 755 {} +; find $R/usr/share -type f -exec chmod 644 {} +
mkdir -p /src/dist
dpkg-deb --root-owner-group -Zxz --build $R /src/dist/MacBar-Ubuntu.deb
dpkg-deb --info /src/dist/MacBar-Ubuntu.deb | sed -n '1,12p'
dpkg-deb --contents /src/dist/MacBar-Ubuntu.deb | awk '{print $1, $6}'
