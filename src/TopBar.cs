using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace MacBar
{
    /// <summary>Shared plumbing for the two bars: no-activate tool window, app-bar registration, tint and blur.</summary>
    public abstract class BarWindow : Window
    {
        protected IntPtr Hwnd;
        protected readonly AppBar AppBar = new AppBar();
        protected readonly Settings S;
        protected readonly Border Bar = new Border();
        DispatcherTimer reposTimer;

        protected BarWindow(Settings s)
        {
            S = s;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize; ShowActivated = false;
            FontFamily = Theme.Font; UseLayoutRounding = true;
            // Pixel-snapped text/glyph rendering; WPF's default "Ideal" mode looks soft at small sizes.
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            Content = Bar;
            SourceInitialized += delegate
            {
                Hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(Hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
                HwndSource.FromHwnd(Hwnd).AddHook(Hook);
            };
        }

        public double Scale
        {
            get { var src = PresentationSource.FromVisual(this); return src != null ? src.CompositionTarget.TransformToDevice.M11 : 1; }
        }

        IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == AppBar.CallbackMessage)
            {
                int code = w.ToInt32();
                if (code == AppBar.ABN_POSCHANGED) DeferReposition();
                // ABN_FULLSCREENAPP is unreliable on Windows 11; App polls for full-screen windows instead.
                handled = true;
            }
            else if (msg == Native.WM_DISPLAYCHANGE) DeferReposition();
            else if (msg == 0x21 /* WM_MOUSEACTIVATE */) { handled = true; return new IntPtr(3); /* MA_NOACTIVATE */ }
            return IntPtr.Zero;
        }

        public void DeferReposition()
        {
            if (reposTimer == null)
            {
                reposTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                reposTimer.Tick += delegate { reposTimer.Stop(); Reposition(); };
            }
            reposTimer.Stop(); reposTimer.Start();
        }

        public abstract void Reposition();

        public virtual void ApplyAppearance()
        {
            Bar.Background = Theme.BarTint(S.Opacity);
            Theme.ApplyBlur(Hwnd, S.Blur);
        }

        /// <summary>Called after Explorer restarts: app bars are forgotten and must be re-registered.</summary>
        public void ResetAppBar() { AppBar.Reset(); Reposition(); }

        public void ReleaseAppBar() { AppBar.Unregister(); }

        protected Border Item(UIElement child)
        {
            var b = new Border
            {
                Child = child, Padding = new Thickness(7, 0, 7, 0), Margin = new Thickness(1, 3, 1, 3),
                CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Stretch
            };
            b.MouseEnter += delegate { b.Background = Theme.Hover; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            return b;
        }
    }

    public class TopBar : BarWindow
    {
        public const double BarHeight = 28;
        readonly TrayHost tray;
        readonly WeatherService weather;
        readonly AudioController audio = new AudioController();
        readonly NetworkState net = new NetworkState();
        readonly BatteryState battery = new BatteryState();

        readonly DockPanel root = new DockPanel { LastChildFill = true };
        readonly StackPanel left = new StackPanel { Orientation = Orientation.Horizontal };
        readonly StackPanel right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        readonly StackPanel trayPanel = new StackPanel { Orientation = Orientation.Horizontal };
        Border logoItem, weatherItem, wifiItem, soundItem, batteryItem, dateItem, timeItem;
        TextBlock appName, weatherGlyph, weatherTemp, batteryText, dateText, timeText;
        string datePattern = "ddd MMM d", timePattern = "h:mm tt";
        WinEventProc fgProc; IntPtr fgHook;
        bool suppressNextUp;

        public TopBar(Settings s, TrayHost tray, WeatherService weather) : base(s)
        {
            this.tray = tray; this.weather = weather;
            Height = BarHeight; Width = 800; Left = 0; Top = 0;
            // The Now Playing island floats in the (otherwise empty) center of the bar.
            var layers = new Grid();
            layers.Children.Add(root);
            layers.Children.Add(new NowPlayingIsland());
            Bar.Child = layers;
            DockPanel.SetDock(right, Dock.Right);
            root.Children.Add(right);
            root.Children.Add(left);
            Build();
            UpdateCulture();

            SourceInitialized += delegate
            {
                ApplyAppearance();
                Reposition();
                fgProc = OnForeground;
                fgHook = Native.SetWinEventHook(3, 3, IntPtr.Zero, fgProc, 0, 0, 2 /* SKIPOWNPROCESS */);
                OnForeground(IntPtr.Zero, 3, Native.GetForegroundWindow(), 0, 0, 0, 0);
            };
            Closed += delegate { if (fgHook != IntPtr.Zero) Native.UnhookWinEvent(fgHook); };

            var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clock.Tick += delegate { UpdateClock(); };
            clock.Start();
            var status = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            status.Tick += delegate { PollStatus(false); };
            status.Start();

            if (tray != null) tray.Changed += RebuildTray;
            weather.Updated += UpdateWeather;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += delegate (object o, Microsoft.Win32.UserPreferenceChangedEventArgs e)
            {
                if (e.Category == Microsoft.Win32.UserPreferenceCategory.Locale) Dispatcher.BeginInvoke(new Action(delegate { UpdateCulture(); }));
            };

            MouseRightButtonUp += delegate (object o, MouseButtonEventArgs e)
            {
                if (e.Handled) return;
                var m = new ContextMenu();
                m.Items.Add(MenuItem("MacBar Settings…", delegate { App.Instance.ShowSettings(); }));
                m.Items.Add(MenuItem("Quit MacBar", delegate { App.Instance.Quit(); }));
                m.IsOpen = true;
            };
        }

        static MenuItem MenuItem(string header, Action a)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += delegate { a(); };
            return mi;
        }

        TextBlock Label(string text, bool bold)
        {
            return new TextBlock { Text = text, FontSize = 13, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, Foreground = Theme.Fg, VerticalAlignment = VerticalAlignment.Center };
        }

        void Build()
        {
            left.Children.Clear(); right.Children.Clear();
            left.Effect = Theme.Halo(); right.Effect = Theme.Halo();
            left.Margin = new Thickness(6, 0, 0, 0); right.Margin = new Thickness(0, 0, 6, 0);

            logoItem = Item(Glyphs.Logo());
            ((FrameworkElement)logoItem.Child).VerticalAlignment = VerticalAlignment.Center;
            logoItem.Padding = new Thickness(10, 0, 10, 0);
            logoItem.MouseLeftButtonUp += delegate { App.Trace("logo click"); ShowSystemMenu(); };
            left.Children.Add(logoItem);
            appName = Label("", true);
            appName.TextTrimming = TextTrimming.CharacterEllipsis;
            appName.MaxWidth = 420;
            appName.Margin = new Thickness(6, 0, 0, 0);
            left.Children.Add(appName);

            right.Children.Add(trayPanel);
            RebuildTray();

            var wp = new StackPanel { Orientation = Orientation.Horizontal };
            weatherGlyph = new TextBlock { FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI Emoji"), FontSize = 14, Foreground = Theme.Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            weatherTemp = Label("", false);
            wp.Children.Add(weatherGlyph); wp.Children.Add(weatherTemp);
            weatherItem = Item(wp);
            weatherItem.MouseLeftButtonUp += delegate
            {
                if (Flyout.ToggleOff("weather")) return;
                new WeatherFlyout(weather, delegate { App.Instance.ShowSettings(); }).ShowBelow(weatherItem);
            };
            right.Children.Add(weatherItem);
            UpdateWeather();

            wifiItem = Item(null);
            wifiItem.MouseLeftButtonUp += delegate
            {
                if (Flyout.ToggleOff("wifi")) return;
                if (!WifiControl.HasAdapter) { ShellUtil.Open("ms-settings:network"); return; }
                new WifiFlyout(delegate { PollStatus(true); }).ShowBelow(wifiItem);
            };
            right.Children.Add(wifiItem);

            soundItem = Item(null);
            soundItem.MouseLeftButtonUp += delegate
            {
                if (Flyout.ToggleOff("volume") || !audio.Available) return;
                new VolumeFlyout(audio, delegate { PollStatus(true); }).ShowBelow(soundItem);
            };
            soundItem.MouseWheel += delegate (object o, MouseWheelEventArgs e)
            {
                if (!audio.Available) return;
                audio.SetVolume(audio.Volume + 0.02f * e.Delta / 120f);
                PollStatus(true);
                e.Handled = true;
            };
            right.Children.Add(soundItem);

            var bp = new StackPanel { Orientation = Orientation.Horizontal };
            batteryText = Label("", false);
            batteryText.Margin = new Thickness(0, 0, 5, 0);
            batteryText.FontSize = 12;
            bp.Children.Add(batteryText);
            batteryItem = Item(bp);
            batteryItem.MouseLeftButtonUp += delegate { ShellUtil.Open("ms-settings:batterysaver"); };
            right.Children.Add(batteryItem);

            // Control Center (macOS puts it just left of the clock).
            var cc = Item(Glyphs.ControlCenter());
            cc.ToolTip = "Control Center";
            cc.MouseLeftButtonUp += delegate
            {
                if (Flyout.ToggleOff("control")) return;
                new ControlCenterFlyout(audio, cc, delegate { PollStatus(true); }).ShowBelow(cc);
            };
            right.Children.Add(cc);

            // Date and time share one button, like macOS ("Mon Nov 3  11:14 PM").
            dateText = Label("", false);
            timeText = Label("", false);
            timeText.Margin = new Thickness(8, 0, 0, 0);
            var clock = new StackPanel { Orientation = Orientation.Horizontal };
            clock.Children.Add(dateText); clock.Children.Add(timeText);
            dateItem = timeItem = Item(clock);
            dateItem.Margin = new Thickness(4, 3, 1, 3);
            MouseButtonEventHandler openCalendar = delegate
            {
                if (Flyout.ToggleOff("calendar")) return;
                new CalendarFlyout().ShowBelow(timeItem);
            };
            dateItem.MouseLeftButtonUp += openCalendar;
            right.Children.Add(dateItem);

            PollStatus(true);
            UpdateClock();
        }

        public override void ApplyAppearance()
        {
            base.ApplyAppearance();
        }

        /// <summary>Re-creates every element so new theme colors take effect.</summary>
        public void Rebuild()
        {
            Build();
            ApplyAppearance();
            OnForeground(IntPtr.Zero, 3, Native.GetForegroundWindow(), 0, 0, 0, 0);
        }

        /// <summary>Pixels reserved at the top of the primary screen (0 when windows may use the whole screen).</summary>
        public int ReservedPx;

        public override void Reposition()
        {
            if (Hwnd == IntPtr.Zero) return;
            double sc = Scale;
            int sw = Native.GetSystemMetrics(0);
            int hpx = (int)Math.Round(BarHeight * sc);
            if (!AppBar.Registered) AppBar.Register(Hwnd);
            // Thickness 0 = registered (for full-screen notifications) but windows may use the whole screen.
            int thick = S.MenuBarReservesSpace ? hpx : 0;
            ReservedPx = thick;
            AppBar.SetPos(AppBar.ABE_TOP, new RECT { Left = 0, Top = 0, Right = sw, Bottom = thick }, thick);
            Width = sw / sc;
            Height = BarHeight;
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        void OnForeground(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd == IntPtr.Zero || appName == null) return;
            if (Native.GetPid(hwnd) == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return;
            string cls = Native.GetClass(hwnd);
            if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return;
            string name = AppNames.ForWindow(hwnd);
            if (!string.IsNullOrEmpty(name)) appName.Text = name;
        }

        void ShowSystemMenu()
        {
            var m = new ContextMenu { PlacementTarget = logoItem, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            m.Items.Add(MenuItem("About This PC", delegate { ShellUtil.Open("ms-settings:about"); }));
            m.Items.Add(new Separator());
            m.Items.Add(MenuItem("System Settings…", delegate { ShellUtil.Open("ms-settings:"); }));
            m.Items.Add(MenuItem("Task Manager", delegate { ShellUtil.Open("taskmgr.exe"); }));
            m.Items.Add(MenuItem("MacBar Settings…", delegate { App.Instance.ShowSettings(); }));
            m.Items.Add(new Separator());
            m.Items.Add(MenuItem("Sleep", delegate { Native.SetSuspendState(false, false, false); }));
            m.Items.Add(MenuItem("Restart…", delegate { if (Confirm("restart")) ShellUtil.Open("shutdown.exe /r /t 0"); }));
            m.Items.Add(MenuItem("Shut Down…", delegate { if (Confirm("shut down")) ShellUtil.Open("shutdown.exe /s /t 0"); }));
            m.Items.Add(new Separator());
            m.Items.Add(MenuItem("Lock Screen", delegate { Native.LockWorkStation(); }));
            m.Items.Add(MenuItem("Log Out " + Environment.UserName + "…", delegate { if (Confirm("log out")) ShellUtil.Open("shutdown.exe /l"); }));
            m.Items.Add(new Separator());
            m.Items.Add(MenuItem("Quit MacBar", delegate { App.Instance.Quit(); }));
            m.IsOpen = true;
        }

        static bool Confirm(string action)
        {
            return MessageBox.Show("Are you sure you want to " + action + " now?", "MacBar", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        }

        void RebuildTray()
        {
            trayPanel.Children.Clear();
            if (tray == null || !S.ShowTrayIcons) return;
            foreach (var ic in tray.Icons)
            {
                if (ic.Hidden || ic.Icon == null) continue;
                var icon = ic;
                var img = new Image { Source = ic.Icon, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                var item = Item(img);
                item.Padding = new Thickness(6, 0, 6, 0);
                if (!string.IsNullOrWhiteSpace(ic.Tip)) item.ToolTip = ic.Tip;
                item.MouseLeftButtonDown += delegate (object o, MouseButtonEventArgs e)
                {
                    if (e.ClickCount == 2) { suppressNextUp = true; tray.Click(icon, false, true); }
                };
                item.MouseLeftButtonUp += delegate
                {
                    if (suppressNextUp) { suppressNextUp = false; return; }
                    tray.Click(icon, false, false);
                };
                item.MouseRightButtonUp += delegate (object o, MouseButtonEventArgs e) { e.Handled = true; tray.Click(icon, true, false); };
                trayPanel.Children.Add(item);
            }
        }

        void UpdateWeather()
        {
            var d = weather.Current;
            if (d == null || !S.HasLocation) { weatherItem.Visibility = Visibility.Collapsed; return; }
            weatherItem.Visibility = Visibility.Visible;
            weatherGlyph.Text = WeatherService.Glyph(d.Code, d.IsDay);
            weatherTemp.Text = weather.Temp(d.TempC);
            weatherItem.ToolTip = WeatherService.Describe(d.Code) + (string.IsNullOrEmpty(d.Location) ? "" : " · " + d.Location) + (weather.LastFetchFailed ? " (offline)" : "");
        }

        public void PollStatus(bool force)
        {
            if (audio.Poll() || force)
            {
                soundItem.Visibility = audio.Available ? Visibility.Visible : Visibility.Collapsed;
                int waves = audio.Volume < 0.01 ? 0 : audio.Volume < 0.34 ? 1 : audio.Volume < 0.67 ? 2 : 3;
                var g = Glyphs.Speaker(waves, audio.Muted);
                g.VerticalAlignment = VerticalAlignment.Center;
                soundItem.Child = g;
                soundItem.ToolTip = audio.Muted ? "Sound: muted" : "Volume: " + Math.Round(audio.Volume * 100) + "%";
            }
            if (net.Poll() || force)
            {
                FrameworkElement g;
                if (net.WifiConnected)
                {
                    g = Glyphs.Wifi(net.Signal >= 67 ? 3 : net.Signal >= 34 ? 2 : 1, true);
                    wifiItem.ToolTip = "Wi-Fi: " + net.Ssid + " (" + net.Signal + "%)";
                }
                else if (net.Ethernet)
                {
                    g = Glyphs.Ethernet();
                    wifiItem.ToolTip = "Ethernet: connected";
                }
                else
                {
                    g = Glyphs.Wifi(0, false);
                    wifiItem.ToolTip = net.HasWifiAdapter ? "Wi-Fi: not connected" : "No network connection";
                }
                g.VerticalAlignment = VerticalAlignment.Center;
                wifiItem.Child = g;
            }
            if (battery.Poll() || force)
            {
                batteryItem.Visibility = battery.Present ? Visibility.Visible : Visibility.Collapsed;
                var bp = (StackPanel)batteryItem.Child;
                while (bp.Children.Count > 1) bp.Children.RemoveAt(1);
                var g = Glyphs.Battery(battery.Percent, battery.Charging);
                g.VerticalAlignment = VerticalAlignment.Center;
                bp.Children.Add(g);
                batteryText.Text = battery.Percent + "%";
                batteryText.Visibility = S.ShowBatteryPercent ? Visibility.Visible : Visibility.Collapsed;
                batteryItem.ToolTip = "Battery: " + battery.Percent + "%" + (battery.Charging ? " (charging)" : battery.OnAc ? " (plugged in)" : "");
            }
        }

        void UpdateCulture()
        {
            try
            {
                var sb = new StringBuilder(85);
                Native.GetUserDefaultLocaleName(sb, sb.Capacity);
                CultureInfo.CurrentCulture.ClearCachedData();
                var ci = new CultureInfo(sb.ToString(), true);
                Thread.CurrentThread.CurrentCulture = ci;
                CultureInfo.DefaultThreadCurrentCulture = ci;
                string md = ci.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM");
                datePattern = "ddd " + md;
                timePattern = ci.DateTimeFormat.ShortTimePattern;
            }
            catch { }
            UpdateClock();
        }

        void UpdateClock()
        {
            if (dateText == null) return;
            var now = DateTime.Now;
            string d = now.ToString(datePattern, CultureInfo.CurrentCulture);
            string t = now.ToString(timePattern, CultureInfo.CurrentCulture);
            if (dateText.Text != d) dateText.Text = d;
            if (timeText.Text != t) timeText.Text = t;
            dateItem.ToolTip = now.ToString("D", CultureInfo.CurrentCulture);
        }
    }
}
