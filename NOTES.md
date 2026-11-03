# MacBar — development notes

A running log of how MacBar was built and why things are the way they are.

## Goal
Replace the Windows 11 taskbar with a macOS-style layout:
- **Dock** (bottom): centered, icons only, translucent, grows/shrinks with the number of apps.
- **Menu bar** (top): system menu + bold active-app name on the left; tray icons, weather,
  Wi‑Fi, sound, battery, date and time on the right.

## Toolchain decision
- No .NET SDK was installed on the dev machine, but every Windows 10/11 PC ships the
  .NET Framework 4.8 C# compiler (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`)
  and WPF. MacBar is built with that, so **building needs no extra installs** and the app
  runs on any Windows 10/11 machine without a runtime download.
- Consequence: the code is limited to **C# 5** (no string interpolation, `?.`, `nameof`,
  expression-bodied members, auto-property initializers) and **no XAML compilation**:
  all UI is built in code; menu/tooltip styles are parsed from a XAML string at runtime.
- Built as x64 so P/Invoke pointer sizes are predictable.

## Architecture (src/)
| File | Purpose |
|---|---|
| `Native.cs` | All Win32 / shell / DWM / WLAN P/Invoke declarations and small helpers. |
| `Settings.cs` | Settings model, JSON load/save to `%APPDATA%\MacBar\settings.json`. |
| `Shell.cs` | Window enumeration (taskbar-eligible windows), app names, icon loading, launching, `.lnk` resolving. |
| `Theme.cs` | Colors, light/dark detection, blur (accent policy), window regions, menu styles; `AppBar` registration; `TaskbarHider`. |
| `TrayHost.cs` | Captures notification-area icons (see below). |
| `Status.cs` | Core Audio volume/mute, Wi‑Fi (wlanapi) + Ethernet state, battery. |
| `Weather.cs` | Open-Meteo weather + geocoding, caching, unit conversion, condition glyphs. |
| `Glyphs.cs` | Vector icons (Wi‑Fi, speaker, battery, Ethernet, system logo). |
| `Flyouts.cs` | Drop-down panels: volume slider, weather forecast. |
| `TopBar.cs` | Shared `BarWindow` base + the top menu bar. |
| `Dock.cs` | The bottom dock. |
| `SettingsWindow.cs` | Settings UI. |
| `App.cs` | Entry point, lifecycle, cleanup. |

## Key techniques
### Hiding the Windows taskbar
- Set the taskbar to auto-hide (`SHAppBarMessage(ABM_SETSTATE)`) so it stops reserving
  screen space, then `ShowWindow(SW_HIDE)` on `Shell_TrayWnd` / `Shell_SecondaryTrayWnd`.
- A 400 ms timer re-hides it if Explorer shows it again (e.g. when Start opens).
- The original auto-hide state is saved to settings **before** changing it, so it can be
  restored on exit, after a crash (next launch), or with `MacBar.exe --restore`.

### Reserving screen space
Both bars register as Windows **app bars** (`ABM_NEW` / `ABM_QUERYPOS` / `ABM_SETPOS`), so
maximized windows stop below the menu bar and above the dock (dock reservation is optional).
`SetPos` skips `ABM_SETPOS` when the rectangle hasn't changed, to avoid notification loops
between our two app bars. `ABN_FULLSCREENAPP` hides both bars for full-screen apps/games.

### Tray icons
Windows 11 draws its tray in XAML with no public API to enumerate icons. MacBar uses the
approach alternative shells use:
1. Register a window class named `Shell_TrayWnd` in our process and create a hidden,
   topmost window with it. `Shell_NotifyIcon` sends `WM_COPYDATA` to the *first* window of
   that class in Z-order, which is now ours.
2. Parse the `NOTIFYICONDATA` (32-bit handle layout) to track add/modify/delete/version.
3. **Forward every `WM_COPYDATA` unchanged to Explorer's real taskbar**, so Explorer and
   the apps behave exactly as before (this also covers `SHAppBarMessage`, which uses the
   same channel).
4. Broadcast `TaskbarCreated` so already-running apps re-add their icons.
5. Clicks are delivered to the owning app via its callback message, matching Explorer's
   format for `NOTIFYICON_VERSION_4` vs older apps, after `AllowSetForegroundWindow`.
A 3 s timer re-claims the top Z-order spot if Explorer restarts, and prunes icons whose
owner window died.

### Blur and transparency
- Tint is drawn by WPF (`AllowsTransparency`), opacity 0–100 % (default 20 %).
- Blur uses the undocumented `SetWindowCompositionAttribute` accent policy:
  None / Light (`ACCENT_ENABLE_BLURBEHIND`) / Strong (`ACCENT_ENABLE_ACRYLICBLURBEHIND`).
- Readability on any wallpaper: a soft drop-shadow "halo" in the contrasting color behind
  text and icons, plus a faint border on the dock.

### Status items
- **Wi‑Fi**: `WlanQueryInterface(wlan_intf_opcode_current_connection)` → SSID + signal
  quality (offset 576 in `WLAN_CONNECTION_ATTRIBUTES`). Falls back to an Ethernet glyph.
  Click → `ms-availablenetworks:` (Windows network flyout).
- **Sound**: Core Audio `IAudioEndpointVolume` via hand-written COM interop. Click → custom
  volume flyout with slider/mute; mouse wheel changes volume ±2 % per notch.
- **Battery**: `GetSystemPowerStatus`; hidden when `BatteryFlag == 128` (no battery).
  Click → `ms-settings:batterysaver`.
- **Date/time**: formats come from the user's Windows region settings (`MonthDayPattern`
  with abbreviated month + `ShortTimePattern`), refreshed when the locale changes.
  Click → Win+N (calendar + notifications).
- **Weather**: [Open-Meteo](https://open-meteo.com) — free, no API key. Location is chosen
  manually in Settings via Open-Meteo geocoding (no Windows location access). Stored in °C,
  displayed in °F (default) or °C. Refreshes every 30 min, cached to
  `%APPDATA%\MacBar\weather.json`; when offline the last data is shown and marked offline;
  hidden if nothing has ever loaded.

### Full-screen hiding
macOS hides the menu bar and dock for full-screen apps. Windows' `ABN_FULLSCREENAPP`
notification proved unreliable on Windows 11, so `App.CheckFullscreen` polls every 500 ms:
if the foreground window is on the primary monitor, covers the whole screen and has **no
caption** (F11 browsers, video players, games), both bars hide. Maximized windows have a
caption, so they don't trigger it.

### Installer
`installer/Setup.cs` is a small WinForms program with `MacBar.exe` embedded as a resource
(`/resource:` at compile time), so the installer is one self-contained `.exe`.
- Per-user install to `%LOCALAPPDATA%\Programs\MacBar`, `asInvoker` manifest → no UAC prompt
  (the manifest also stops Windows' "setup.exe needs admin" heuristic).
- Start menu: MacBar, Restore Windows Taskbar, Uninstall MacBar.
- `HKCU\...\Uninstall\MacBar` entry so it shows in Apps & Features; optional `HKCU\...\Run` entry.
- The installer copies itself as `Uninstall MacBar.exe`; `/uninstall` stops MacBar, runs
  `MacBar.exe --restore`, removes everything, and deletes itself via a delayed `cmd` call.
- Before replacing files it kills any running MacBar and restores the taskbar first.

## Log
- **2026-11-02** — Project started. Wrote Native, Settings, Shell, Theme (+AppBar,
  TaskbarHider), TrayHost, Status, Weather, Glyphs, Flyouts, TopBar, Dock, SettingsWindow, App.
  Compiled first try with the built-in `csc.exe`.
- First run: both bars, tray capture, dock grouping and icons all worked.
  - Bug: File Explorer pinned twice. Windows 11's Explorer taskbar pin is a shell shortcut
    with no target path (only AUMID `Microsoft.Windows.Explorer`), so the "is Explorer already
    pinned?" check missed it. Fixed the import + a one-time cleanup of existing settings.
- Feedback round 1:
  - *Icons too tight* → dock spacing now scales with icon size (~7 px at 48 px), more bar
    padding, slightly wider tray icon hit areas.
  - *Windows should use the whole screen (FancyZones)* → the dock no longer reserves space by
    default. Both bars still register as app bars but with **zero thickness** when not reserving,
    so Windows still treats them as app bars.
  - *Desktop icons hidden under the top bar* → the menu bar reserves its 28 px strip by default
    (like macOS, where windows never go under the menu bar). Both behaviors are toggles in Settings.
  - *Sound panel opened on the other monitor* → the flyout measured its width before it existed
    (width 0), so the right-edge clamp failed. Now it is shown off-screen, laid out, then clamped
    to the primary screen.
- Bug: `MacBar.exe --restore` hung forever on a multi-monitor PC. Showing a taskbar window
  while iterating with `FindWindowEx(..., after, ...)` reorders the Z-order, so the walk never
  ended. Now all taskbar windows are collected first, then shown/hidden with `ShowWindowAsync`.
  Found because the uninstaller's restore step never returned.
- Feedback round 2:
  - *Hide on full screen like macOS* → custom full-screen detection (see above).
  - *Redesign Wi‑Fi, notifications and calendar (macOS + Linux inspired)*:
    - Wi‑Fi panel (macOS Control Center style): on/off switch, connected network in a blue badge,
      Known / Other Networks sections, lock icons, connect via saved profile (`WlanConnect`),
      disconnect, background scan (`WlanScan`), refreshes every 3 s. New networks hand off to the
      Windows panel for the password.
    - Calendar panel (GNOME date-menu style): weekday + long date header, month grid that follows
      the region's first day of week, today in an accent circle, month navigation + Today.
    - Notifications: Windows only lets packaged apps read notifications (`UserNotificationListener`
      needs package identity), so the panel has a Notifications card with "Show Notifications"
      (Win+N) and "Do Not Disturb…" buttons instead of a list.
- Installer built and tested with `/quiet` install + uninstall (files, shortcuts, registry
  entries all created and removed).
- Published v1.0.0 to GitHub.

### 2026-11-03 — feedback round 3 (v1.1.0)
- *Icons look a little blown up* → default dock icon size 48 → 44 px (existing settings migrated
  via a new `SettingsVersion` field), menu-bar glyphs scaled to 92 %.
- *Icon quality* → the dock used to ask the shell for 96 px icons and let WPF shrink them, i.e. two
  resampling passes. Now `IconLoader.LoadSharp` takes the 256 px icon and downsamples it **once**
  with GDI+ high-quality bicubic to the exact on-screen pixel size, so WPF draws it 1:1. Old apps
  that only have small icons come back as a small image in the corner of a 256 canvas; those are
  detected (content ≤ 1/3 of the canvas) and cropped first.
- *Sound has no output switcher* → the sound panel lists active playback devices
  (`IMMDeviceEnumerator.EnumAudioEndpoints`) with the current one highlighted. Switching uses
  `IPolicyConfig.SetDefaultEndpoint` for all three roles. It's undocumented, but it's what Windows'
  own sound settings use and has been stable since Windows 7.
- *Date and time should be one button* → merged into a single item ("Tue Nov 3  12:05 AM").
- *Clicking an icon again should close its panel* → clicking the bar doesn't activate MacBar, so an
  open panel never lost focus and the click opened a fresh copy. `Flyout.ToggleOff(key)` closes
  the open panel instead.
- *Taskbar not automatically hiding* → the dock now auto-hides like macOS by default: it slides
  below the screen edge (~180 ms smoothstep) and comes back when the cursor touches the bottom
  edge of the primary screen. It stays up while hovered, while a dock menu is open, or while
  dragging. Settings → "Hide the dock": Never / Always (macOS) / When a window overlaps it
  (Ubuntu-style "intellihide": hides only when the focused window covers the dock area).
- Bug: open File Explorer windows showed up as a separate dock icon because the Windows 11
  Explorer pin has only an AUMID and Explorer windows have none. Special-cased in `DockItem.Matches`.
- Repo: removed the Claude co-author trailer from history at the user's request.
