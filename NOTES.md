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
- Repo: removed the Claude co-author trailer from history at the user's request (amended the
  first commit, force-pushed, re-pointed the v1.0.0 tag).
- Process bug: patching files with PowerShell 5.1 `Get-Content -Raw` reads UTF-8 (no BOM) as
  Windows-1252, which garbled "…", "→", "‑" (e.g. menu text "Settings…"). Repaired by reversing
  the mis-decoding; all edits now go through an encoding-safe editor.

### 2026-11-03 — feedback round 4: Control Center, Now Playing, sharper icons
- **WinRT from the built-in compiler.** Bluetooth/airplane/media need WinRT APIs. `csc` can reference
  the `.winmd` files in `C:\Windows\System32\WinMetadata`, but `await` on WinRT operations normally
  needs `System.Runtime.WindowsRuntime`, which expects the SDK's merged `Windows.winmd` (not on
  normal PCs). Solution: our own `GetAwaiter` extension for `IAsyncOperation<T>` (`WinRt.cs`) that
  completes a `TaskCompletionSource` from the operation's `Completed` callback.
- **Control Center** (`ControlCenter.cs`), modeled on macOS:
  - Connectivity module: Wi‑Fi and Bluetooth via `Windows.Devices.Radios` (`Radio.SetStateAsync`);
    Airplane Mode via the RadioManagement COM API (`IRadioManager`, CLSID 581333F6…), the same one
    Quick Settings uses. Circle = toggle; clicking "Wi‑Fi" opens the network list.
  - Tiles: Do Not Disturb (no public API to toggle it → opens notification settings) and Screen
    Mirroring (Win+K cast flyout).
  - Display brightness: laptop panels via WMI `WmiMonitorBrightnessMethods`, external monitors via
    DDC/CI (`dxva2` `SetMonitorBrightness`, ~60 ms per call → background writer that only sends the
    latest value). Hidden if the monitor doesn't support either.
  - Sound slider and a Now Playing card.
- **Now Playing "island"** (`NowPlaying.cs`): black pill centered in the menu bar (the empty middle of
  the ultrawide), à la Dynamic Island: album art, title, animated green equalizer bars while playing;
  hover widens it to show artist + prev/play/next; click drops a player card with the album art
  blurred (60 px Gaussian) and darkened as the backdrop, big art, "Playing from Spotify", a seekable
  progress bar (position is extrapolated between the app's sparse timeline updates) and transport
  controls. Uses `GlobalSystemMediaTransportControlsSessionManager`; album art read through
  `DataReader` because `AsStreamForRead` isn't available without the SDK.
- **Blurry icons, round 2:**
  - Menu bar: the hand-drawn vector glyphs sat on fractional pixels. Replaced with Windows 11's
    **Segoe Fluent Icons** font (hinted for 16 px), layered like the real taskbar (faint full glyph +
    signal/volume level on top), with `TextFormattingMode.Display` on all bars and panels. Removed
    the 0.92 scale transform, which itself was blurring things.
  - Dock: shrinking 256 → 44 px loses edge contrast; added a light 3×3 unsharp mask after the
    bicubic downsample (color clamped to alpha because the pixels are premultiplied).
- Published v1.1.0.

### 2026-11-03 — feedback round 5: app page + dock Apps button (v1.2.0)
(The user clarified they did *not* want a new app switcher; Windows' Alt+Tab stays.)
- **App page** (`Launchpad.cs`), modeled on macOS Launchpad:
  - App list = the shell's `shell:AppsFolder` (same source as Start → All apps, covers Store and
    desktop apps), read through `Shell.Application` COM on a background STA thread. Uninstallers,
    readmes, help files and web links are filtered out; duplicates by name dropped; sorted A–Z.
  - Icons load on that thread too (the icon cache got a lock), and the list is pre-warmed 6 s after
    startup so the page opens instantly. Only the resized 72 px icons are cached; caching the 256 px
    originals for ~300 apps would cost ~75 MB.
  - Full-screen window with acrylic blur + dark tint, search pill (live filter ranked: starts-with →
    word-starts-with → contains; Enter opens the top hit), paged grid that fills the screen (9 columns
    on ultrawide, 7 otherwise, up to 5 rows), page dots above the dock, wheel/arrow/PgUp/PgDn paging
    with a slide animation, zoom/fade in and out, Esc clears search then closes, clicking empty space
    closes (checked by walking up from the clicked element, because the search box is made of Grids
    too). Right-click an app → Open / Keep in Dock (desktop apps' `{KnownFolder}\path.exe` is expanded
    via `SHGetKnownFolderPath` so the new pin matches running windows).
- **Apps button**: first item in the dock, a vector tile with a 3×3 grid of colored squares; toggles
  the app page.
- **Icon fix (also fixes a dark square in the dock):** for apps that only ship small icons, asking the
  shell for 256 px returns either a solid plate or (Bambu Studio, Cisco Packet Tracer, ASUS DriverHub…)
  a *faint white outline frame* around an empty square with the icon tiny in the middle. `Padding()`
  detects both; framed → always use the 48 px icon, plated → use 48 px only if it isn't the same plate
  (some real icons are solid squares). Icons with no alpha channel now treat pure black as transparent.

## Round 6 – app page performance (1.2.1)
- Feedback: the app page was choppy and dropped frames when opening and paging.
- Cause: it was a full-screen `AllowsTransparency=true` window. WPF draws those as per-pixel-alpha
  layered windows, copying the whole 3440×1440 frame back to system memory on every frame. On top of
  that, every icon *and* every label had its own `DropShadowEffect` (~90 shaders per page, plus the
  off-screen pages).
- Fix:
  - Normal (non-layered) window. Transparency comes from DWM: the frame is extended over the whole
    client area (`DwmExtendFrameIntoClientArea(-1)`) with a transparent `CompositionTarget`, and
    acrylic stays on top.
  - `Window.Opacity` doesn't work without layering, so open/close fade the content root instead.
    `WS_EX_LAYERED` + `SetLayeredWindowAttributes` was tried and rejected: WPF's hardware output never
    shows up in such a window, so the page was invisible.
  - Each page is a `BitmapCache` (at device DPI), so sliding and zooming move textures.
  - Removed the label shadows; the dark tint already makes the labels readable.
- Measured with `MACBAR_DEBUG=1` (the log records frames per slide). After the fix, page slides run
  at ~70 fps, with at most one skipped frame.
- Control Center: the Screen Mirroring tile touched the Display card. Only the connectivity card had
  a bottom margin, and the stacked icon-over-label tiles made the right column the taller one. Moved
  the gap to the row, and the tiles now put the icon beside the label (macOS layout).
- Dock: NoMachine showed a blank page. Its windows belong to `nxplayer.bin`, and the shell only uses
  a program's own icon for `.exe` files, so a `.bin` gets the generic file icon. For a non-.exe (or
  an .exe with no icons), the dock now looks for a Start menu shortcut to the same exe or the same
  folder (`ShellUtil.ShortcutForIconless`). It uses that shortcut's icon, its name ("NoMachine"
  instead of "NoMachine Player Process"), and launches through it.

## Round 7 – Windows key, local search, 1.3.0
- Feedback: the GitHub download had no app page (the newest *release* was still 1.1.0; 1.2.x were
  only pushed as commits). Fixed by publishing 1.3.0 as a release.
- **Windows key → app page (and dock):** `WinKey.cs` installs a low-level keyboard hook on its own
  thread, so a busy UI thread can't make Windows skip it. A lone Win press/release (nothing else in
  between) opens or closes the app page. To keep the Windows Start menu closed, the hook swallows
  that Win *release* and replays it after an unassigned key (VK 0xE8); Windows only opens Start
  when Win comes up with nothing pressed in between. Our own injected keys carry a marker in
  `dwExtraInfo` and pass straight through. Win+R, Win+E, Win+Shift+S etc. are untouched. Tested:
  Win toggles the page, Start never appears, Win+R opens Run.
  - The page is activated right after our own injected input, so Windows lets it take focus.
  - The dock is held up (`DockWindow.HoldShown`) and raised above the page while it's open.
- **Search: apps, Settings and files, with nothing from the web.** While typing, the pages are
  replaced by sections:
  - Applications (up to two rows).
  - Settings (`SettingsCatalog`: ~75 `ms-settings:` pages matched by name and keywords, e.g.
    "blue" → Bluetooth & devices and Night light).
  - Files & Folders, from the local Windows Search index (`Search.CollatorDSO` over OLE DB, scoped
    to the user profile; AppData, dot-folders and node_modules are skipped). Queries run on a
    background thread with a short debounce, and only the newest query's results are shown.
  - Enter opens the top result. Right-click a file → Show in File Explorer.
