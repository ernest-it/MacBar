ARG BASE=ubuntu:24.04
FROM ${BASE}
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends \
    gnome-shell gnome-shell-extension-prefs gnome-shell-extensions dbus-x11 dbus \
    libgl1-mesa-dri mesa-utils gir1.2-soup-3.0 gjs \
    gnome-settings-daemon gsettings-desktop-schemas adwaita-icon-theme-full yaru-theme-gnome-shell \
    gnome-text-editor gnome-calculator fonts-cantarell fonts-ubuntu unzip ca-certificates librsvg2-common pulseaudio pulseaudio-utils gir1.2-adw-1 nautilus \
    && rm -rf /var/lib/apt/lists/*
RUN rm -rf /run/systemd/seats && mkdir -p /tmp/.X11-unix && chmod 1777 /tmp/.X11-unix && useradd -m tester
USER tester
WORKDIR /home/tester
