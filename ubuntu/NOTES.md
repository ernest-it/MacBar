# MacBar for Ubuntu: development notes

A GNOME Shell extension (GNOME 45–49, ESM modules). It's an extension rather than a separate app
because on Wayland only the shell can reserve screen space, draw over full-screen windows and move
other apps' windows.

## Layout
- `extension.js`: enable/disable. Hides GNOME's startup overview, which Ubuntu Dock normally does.
- `lib/topbar.js`: restyles the panel and rearranges it into the menu-bar layout.
  - Contents: the system menu, app name, clock on the right, full-screen peek, Super → app grid and
    hot-corner off.
- `lib/dock.js`: the dock. Chrome (`addChrome`) plus `trackChrome` for input, `AppFavorites` for pins,
  `DND` for reordering, and pointer polling (100 ms) for auto-hide.
- `lib/sound.js`: the shell's own Gvc mixer (`Volume.getMixerControl()`).
- `lib/weather.js`: Open-Meteo. `lib/lighting.js`: the OpenRGB SDK protocol (same as the Windows
  version). `lib/nowplaying.js`: MPRIS over D-Bus.
- `lib/workspaces.js`: Workspaces. The JSON lives in `~/.config/macbar/workspaces.json`, shared
  with `prefs.js`.
- `selftest.js`: loaded only when GNOME Shell runs with `MACBAR_SELFTEST=<dir>`. It's left out of
  the release zip.

## Things learned while testing (headless GNOME Shell in Docker)
- The container needs:
  - a system D-Bus
  - `/run/systemd/seats` removed, or GNOME Shell tries logind and crashes
  - `--no-x11` (or a writable `/tmp/.X11-unix`)
  - `librsvg2-common`, or every symbolic icon is blank
  - `ca-certificates`, for the weather fetch
- GNOME clears `Main.panel`'s inline style during startup, after extensions are enabled. MacBar
  watches `notify::style` and puts its own back.
- A `Shell.BlurEffect` (background mode) behind the top panel paints black. Tried on the panel, the
  panel box and a separate layer; on the dock it works. So the menu bar is only tinted.
- GNOME 46: `Main.layoutManager._updateVisibility()` re-hides `panelBox` (tracked with
  `trackFullscreen`) after the peek.
- Mutter 48/49: `move_resize_frame` to a size below the app's minimum drops the move as well, so
  Workspaces follows it with `move_frame`. A window bigger than asked counts as in place.
- Workspaces re-checks windows at 0.6/1.5/3/6 s and must skip full-screen windows, or it undoes a
  user's full screen.
- GNOME 49 already has the overview open when extensions load. Setting `hasOverview = false` isn't
  enough, so it's hidden at startup-complete and for 3 s after.
- GNOME's top-left hot corner opened the overview whenever the pointer reached the logo menu, so
  MacBar turns `enable-hot-corners` off while it runs and restores it when disabled.
- `WM_SETTINGCHANGE`-style problems from the Windows version (FancyZones) don't apply: GNOME owns the
  work area.

## Test results (1.0.0)
26/26 checks on Ubuntu 24.04 (GNOME 46.2), 25.04 (48.1) and 25.10 (49.0). The checks:
- dock and menus
- weather
- Workspaces: arrange only, maximized, reopen a missing app, no duplicates on a second run, capture
- full screen hides both bars; the top-edge peek shows the menu bar and hides it again
- the app grid shows both bars
- the prefs window opens

`install.sh` was run from the release zip and the extension came up ACTIVE with no JS errors.

Not testable in the container: OpenRGB (no SDK server; the indicator correctly stays hidden), real
Chrome profiles, a real GPU (dock blur), and multiple monitors.
