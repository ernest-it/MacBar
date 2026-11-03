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
[assembly: System.Reflection.AssemblyVersion("1.3.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.3.0.0")]

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
        }

        public void OnFullscreen(bool fullscreen)
        {
            var v = fullscreen ? Visibility.Hidden : Visibility.Visible;
            if (top != null) top.Visibility = v;
            if (dock != null) dock.Visibility = v;
        }

        public Window MainBar { get { return top; } }

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
                    if (launchpad.IsVisible) dock.BringToFront();
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
