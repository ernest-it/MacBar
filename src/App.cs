using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("MacBar")]
[assembly: System.Reflection.AssemblyProduct("MacBar")]
[assembly: System.Reflection.AssemblyDescription("macOS-style menu bar and dock for Windows")]
[assembly: System.Reflection.AssemblyVersion("1.4.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.4.1.0")]

namespace MacBar
{
    public class App : Application
    {
        public static App Instance;
        public Settings S;
        public WeatherService Weather;
        readonly TrayHost tray = new TrayHost();
        TopBar top;
        DockWindow dock;
        SettingsWindow settingsWindow;
        bool cleanedUp, trayRunning;
        IntPtr lastRealTray;
        static Mutex mutex;

        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Any(a => a.Equals("--restore", StringComparison.OrdinalIgnoreCase)))
            {
                // Emergency restore: brings the Windows taskbar back even if MacBar was killed.
                TaskbarHider.Restore(Settings.Load());
                return 0;
            }
            Demo.On = args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
            bool created;
            mutex = new Mutex(true, "Local\\MacBar.SingleInstance", out created);
            if (!created) return 0;
            var app = new App();
            app.Run();
            GC.KeepAlive(mutex);
            return 0;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Instance = this;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            S = Settings.Load();
            if (Demo.On) { Demo.Apply(S); Demo.ShowBackdrop(); }
            if (!S.PinsImported)
            {
                S.Pinned = ShellUtil.ImportTaskbarPins();
                S.PinsImported = true;
                S.Save();
            }
            // Older imports could pin File Explorer twice (explorer.exe + Windows 11's AUMID shortcut).
            bool hasShellExplorer = S.Pinned.Exists(p => string.Equals(p.Aumid, "Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase));
            if (hasShellExplorer && S.Pinned.RemoveAll(p => p.Aumid == null && p.ExePath != null &&
                    p.ExePath.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase)) > 0)
                S.Save();
            Theme.Update(S);

            AppDomain.CurrentDomain.UnhandledException += delegate (object o, UnhandledExceptionEventArgs ex) { Log(ex.ExceptionObject as Exception); Cleanup(); };
            DispatcherUnhandledException += delegate (object o, DispatcherUnhandledExceptionEventArgs ex) { Log(ex.Exception); ex.Handled = true; };
            SessionEnding += delegate { Cleanup(); };

            if (S.ShowTrayIcons) { tray.Start(); trayRunning = true; }
            TaskbarHider.Hide(S);
            lastRealTray = Native.FindRealTray(tray.Hwnd);

            Weather = new WeatherService(S);
            top = new TopBar(S, tray, Weather);
            top.Show();
            dock = new DockWindow(S);
            dock.Show();
            Weather.Refresh();
            Workspaces.Load();
            Workspaces.RunAtSignIn(Dispatcher);
            // A lone Windows key press opens the app page instead of the Windows Start menu.
            WinKey.Start(Dispatcher, delegate { if (!cleanedUp) ToggleAppsPage(); });

            // Warm up the app page's list and icons in the background so it opens instantly.
            var warm = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            warm.Tick += delegate
            {
                warm.Stop();
                var src = PresentationSource.FromVisual(top);
                double sc = src != null ? src.CompositionTarget.TransformToDevice.M11 : 1;
                AppCatalog.Refresh(Dispatcher, (int)Math.Round(72 * sc));
            };
            warm.Start();

            SystemEvents.UserPreferenceChanged += delegate (object o, UserPreferenceChangedEventArgs ev)
            {
                if (ev.Category == UserPreferenceCategory.General && S.Theme == "Auto")
                    Dispatcher.BeginInvoke(new Action(delegate
                    {
                        bool wasDark = Theme.Dark;
                        Theme.Update(S);
                        if (wasDark != Theme.Dark) ApplySettings();
                    }));
            };
            SystemEvents.DisplaySettingsChanged += delegate
            {
                Dispatcher.BeginInvoke(new Action(delegate { top.DeferReposition(); dock.DeferReposition(); }));
            };

            // Explorer restart: app bars are forgotten and the taskbar comes back, so redo setup.
            var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            watchdog.Tick += delegate
            {
                EnsureWorkArea();
                IntPtr real = Native.FindRealTray(tray.Hwnd);
                if (real == IntPtr.Zero || real == lastRealTray) return;
                lastRealTray = real;
                TaskbarHider.Hide(S);
                top.ResetAppBar();
                dock.ResetAppBar();
                if (trayRunning) tray.AskAppsToReAdd();
            };
            watchdog.Start();

            var fullscreenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            fullscreenTimer.Tick += delegate { CheckFullscreen(); };
            fullscreenTimer.Start();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SystemParametersInfo(int action, int param, ref RECT r, int flags);
        const int SPI_GETWORKAREA = 0x30, SPI_SETWORKAREA = 0x2F, SPIF_SENDCHANGE = 2;

        /// <summary>
        /// Explorer is supposed to shrink the desktop work area for our app bars, but with its own taskbar
        /// hidden it recalculates lazily and can end up with no strip (desktop icons under the menu bar)
        /// or a stale one. So check it ourselves and set it when it's wrong.
        /// </summary>
        void EnsureWorkArea()
        {
            if (top == null || dock == null || cleanedUp) return;
            int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
            var want = new RECT { Left = 0, Top = top.ReservedPx, Right = sw, Bottom = sh - dock.ReservedPx };
            var cur = new RECT();
            SystemParametersInfo(SPI_GETWORKAREA, 0, ref cur, 0);
            if (cur.Left == want.Left && cur.Top == want.Top && cur.Right == want.Right && cur.Bottom == want.Bottom) return;
            App.Trace(string.Format("work area {0},{1}-{2},{3} -> {4},{5}-{6},{7}", cur.Left, cur.Top, cur.Right, cur.Bottom, want.Left, want.Top, want.Right, want.Bottom));
            // Set it quietly: a system-wide change broadcast makes Explorer recalculate (and drop our strip
            // again). Then tell only the desktop, so its icons move out from under the menu bar.
            SystemParametersInfo(SPI_SETWORKAREA, 0, ref want, 0);
            IntPtr r;
            foreach (var desk in new[] { Native.GetShellWindow(), Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null) })
                if (desk != IntPtr.Zero) Native.SendMessageTimeout(desk, 0x001A /* WM_SETTINGCHANGE */, new IntPtr(SPI_SETWORKAREA), IntPtr.Zero, 2, 500, out r);
            NotifyWorkAreaChanged();
        }

        /// <summary>
        /// Tells every other app (except Explorer, see above) that the work area changed. Window managers like
        /// PowerToys FancyZones size their zones from the work area and otherwise keep the full-screen one they
        /// read before the menu bar reserved its strip, so snapped windows end up under the menu bar.
        /// </summary>
        void NotifyWorkAreaChanged()
        {
            var skip = new System.Collections.Generic.HashSet<uint> { myPid };
            IntPtr shell = Native.GetShellWindow();
            if (shell != IntPtr.Zero) skip.Add(Native.GetPid(shell));
            IntPtr realTray = Native.FindRealTray(tray != null ? tray.Hwnd : IntPtr.Zero);
            if (realTray != IntPtr.Zero) skip.Add(Native.GetPid(realTray));
            var targets = new System.Collections.Generic.List<IntPtr>();
            Native.EnumWindows((h, l) => { if (!skip.Contains(Native.GetPid(h))) targets.Add(h); return true; }, IntPtr.Zero);
            // WM_SETTINGCHANGE can't be posted; send it off the UI thread so a slow app can't stall the bars.
            var t = new System.Threading.Thread(() =>
            {
                IntPtr r;
                foreach (var h in targets)
                    Native.SendMessageTimeout(h, 0x001A, new IntPtr(SPI_SETWORKAREA), IntPtr.Zero, 2 /* ABORTIFHUNG */, 200, out r);
            });
            t.IsBackground = true;
            t.Start();
        }

        bool fullscreenNow;
        static readonly uint myPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        /// <summary>
        /// Like macOS: hide both bars while the focused window is truly full screen on the primary monitor
        /// (covers the whole screen and has no title bar - F11 browsers, videos, games). Maximized windows don't count.
        /// </summary>
        void CheckFullscreen()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || Native.GetPid(fg) == myPid) return;
            bool fs = false;
            string cls = Native.GetClass(fg);
            if (fg != Native.GetShellWindow() && cls != "Progman" && cls != "WorkerW" && cls != "Shell_TrayWnd")
            {
                RECT r; Native.GetWindowRect(fg, out r);
                long style = Native.GetWindowLongPtr(fg, -16).ToInt64();
                bool hasCaption = (style & 0x00C00000) == 0x00C00000;
                bool onPrimary = Native.MonitorFromWindow(fg, 2) == Native.MonitorFromPoint(new POINT(), 1);
                int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
                fs = onPrimary && !hasCaption && r.Left <= 0 && r.Top <= 0 && r.Right >= sw && r.Bottom >= sh;
            }
            if (fs != fullscreenNow) { fullscreenNow = fs; OnFullscreen(fs); }
        }

        public void ApplySettings()
        {
            Theme.Update(S);
            if (S.ShowTrayIcons && !trayRunning) { tray.Start(); trayRunning = true; }
            else if (!S.ShowTrayIcons && trayRunning) { tray.Stop(); tray.Icons.Clear(); trayRunning = false; }
            top.Rebuild();
            dock.ApplyAppearance();
            dock.RebuildPinned();
            dock.Reposition();
            EnsureWorkArea();
        }

        public void OnFullscreen(bool fullscreen)
        {
            peekTop = false; edgeSince = DateTime.MinValue;
            if (fullscreen)
            {
                if (peekTimer == null)
                {
                    peekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
                    peekTimer.Tick += delegate { PeekTick(); };
                }
                peekTimer.Start();
            }
            else if (peekTimer != null) peekTimer.Stop();
            UpdateBarVisibility();
        }

        // While a full-screen app (game, video) hides the bars: the app page brings both back, and parking the
        // mouse at the top edge slides the menu bar down until the mouse leaves it again.
        DispatcherTimer peekTimer;
        bool peekTop;
        DateTime edgeSince = DateTime.MinValue, awaySince = DateTime.MinValue;

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetClipCursor(out RECT r);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);

        void UpdateBarVisibility()
        {
            bool appsOpen = launchpad != null && launchpad.IsVisible;
            bool showTop = !fullscreenNow || appsOpen || peekTop;
            bool showDock = !fullscreenNow || appsOpen;
            if (top != null)
            {
                var v = showTop ? Visibility.Visible : Visibility.Hidden;
                if (top.Visibility != v) { top.Visibility = v; if (showTop && fullscreenNow) top.BringToFront(); }
            }
            if (dock != null)
            {
                var v = showDock ? Visibility.Visible : Visibility.Hidden;
                if (dock.Visibility != v) { dock.Visibility = v; if (showDock && fullscreenNow) dock.BringToFront(); }
            }
        }

        void PeekTick()
        {
            if (!fullscreenNow || top == null) return;
            POINT p; Native.GetCursorPos(out p);
            int sw = Native.GetSystemMetrics(0);
            DateTime now = DateTime.Now;
            if (!peekTop)
            {
                // Games that keep the cursor locked to their window (while playing) clip it; only peek once it's free.
                RECT clip; bool clipped = GetClipCursor(out clip) &&
                    (clip.Left > Native.GetSystemMetrics(76) || clip.Top > Native.GetSystemMetrics(77) ||
                     clip.Right < Native.GetSystemMetrics(76) + Native.GetSystemMetrics(78) || clip.Bottom < Native.GetSystemMetrics(77) + Native.GetSystemMetrics(79));
                bool atEdge = !clipped && p.Y <= 1 && p.Y >= -2 && p.X >= 0 && p.X < sw;
                if (!atEdge) { edgeSince = DateTime.MinValue; return; }
                if (edgeSince == DateTime.MinValue) edgeSince = now;
                // A short hold, so flicking past the edge mid-game doesn't drop the bar.
                if ((now - edgeSince).TotalMilliseconds < 250) return;
                peekTop = true; awaySince = DateTime.MinValue;
                UpdateBarVisibility();
                return;
            }
            // Keep it while the mouse is on the bar or on anything of ours (open menus, panels), or a panel has focus.
            int barPx = (int)Math.Round(top.ActualHeight * top.Scale) + 6;
            IntPtr under = WindowFromPoint(p);
            bool keep = (p.Y < barPx && p.X >= 0 && p.X < sw) ||
                        (under != IntPtr.Zero && Native.GetPid(under) == myPid) ||
                        Native.GetPid(Native.GetForegroundWindow()) == myPid;
            if (keep) { awaySince = DateTime.MinValue; return; }
            if (awaySince == DateTime.MinValue) awaySince = now;
            if ((now - awaySince).TotalMilliseconds < 500) return;
            peekTop = false; edgeSince = DateTime.MinValue;
            UpdateBarVisibility();
        }

        public Window MainBar { get { return top; } }
        public TrayHost Tray { get { return tray; } }

        LaunchpadWindow launchpad;
        public void ToggleAppsPage()
        {
            if (launchpad == null)
            {
                launchpad = new LaunchpadWindow();
                // The dock comes up with the app page and stays above it, like the Windows key bringing up the taskbar.
                launchpad.IsVisibleChanged += delegate
                {
                    dock.HoldShown = launchpad.IsVisible;
                    // Over a full-screen game the bars are hidden; bring them back with the app page.
                    UpdateBarVisibility();
                    if (launchpad.IsVisible) { top.BringToFront(); dock.BringToFront(); }
                };
                launchpad.Activated += delegate { dock.BringToFront(); };
            }
            launchpad.Toggle();
        }

        public void PinApp(PinnedApp pin)
        {
            if (S.Pinned.Exists(p => string.Equals(p.Target, pin.Target, StringComparison.OrdinalIgnoreCase))) return;
            S.Pinned.Add(pin);
            S.Save();
            dock.RebuildPinned();
        }

        public void ShowSettings()
        {
            if (settingsWindow == null)
            {
                settingsWindow = new SettingsWindow(S);
                settingsWindow.Closed += delegate { settingsWindow = null; };
                settingsWindow.Show();
            }
            settingsWindow.Activate();
        }

        WorkspacesWindow workspacesWindow;

        public void ShowWorkspaces()
        {
            if (workspacesWindow == null)
            {
                workspacesWindow = new WorkspacesWindow();
                workspacesWindow.Closed += delegate { workspacesWindow = null; };
                workspacesWindow.Show();
            }
            workspacesWindow.Activate();
        }

        public void Quit()
        {
            Cleanup();
            Shutdown();
        }

        void Cleanup()
        {
            if (cleanedUp) return;
            cleanedUp = true;
            try { WinKey.Stop(); } catch { }
            // Give the screen back before the Windows taskbar returns and reserves its own strip.
            try
            {
                var full = new RECT { Left = 0, Top = 0, Right = Native.GetSystemMetrics(0), Bottom = Native.GetSystemMetrics(1) };
                SystemParametersInfo(SPI_SETWORKAREA, 0, ref full, SPIF_SENDCHANGE);
            }
            catch { }
            try { if (top != null) top.ReleaseAppBar(); } catch { }
            try { if (dock != null) dock.ReleaseAppBar(); } catch { }
            try { if (trayRunning) tray.Stop(); } catch { }
            try { TaskbarHider.Restore(S); } catch { }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Cleanup();
            base.OnExit(e);
        }

        public static void Trace(string msg)
        {
            if (Environment.GetEnvironmentVariable("MACBAR_DEBUG") != "1") return;
            try { File.AppendAllText(Path.Combine(Settings.Dir, "debug.log"), DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine); }
            catch { }
        }

        static void Log(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                File.AppendAllText(Path.Combine(Settings.Dir, "error.log"), DateTime.Now.ToString("s") + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
