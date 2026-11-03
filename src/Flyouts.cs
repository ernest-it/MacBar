using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace MacBar
{
    /// <summary>Rounded drop-down panel that closes when it loses focus.</summary>
    public class Flyout : Window
    {
        static readonly Dictionary<string, DateTime> closedAt = new Dictionary<string, DateTime>();
        static Flyout open;
        readonly string key;
        protected readonly StackPanel Body = new StackPanel();
        protected readonly Border Card;

        public Flyout(string key, double width)
        {
            this.key = key;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize; SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = Theme.Font; FontSize = 13; Foreground = Theme.Fg; UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            Content = Card = new Border
            {
                Width = width, Margin = new Thickness(10, 4, 10, 14), Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(12),
                Background = Theme.PopupBg, BorderBrush = Theme.FgAlpha(0x30), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.3 }, Child = Body
            };
            Activated += delegate { App.Trace("flyout activated " + key); };
            Deactivated += delegate { App.Trace("flyout deactivated " + key); if (IsVisible) Close(); };
            Closed += delegate { App.Trace("flyout closed " + key); closedAt[key] = DateTime.Now; if (open == this) open = null; };
            KeyDown += delegate (object s, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); };
        }

        /// <summary>True if this flyout just closed because the user clicked its own icon (so the click should not reopen it).</summary>
        public static bool JustClosed(string key)
        {
            DateTime t;
            return closedAt.TryGetValue(key, out t) && (DateTime.Now - t).TotalMilliseconds < 350;
        }

        /// <summary>
        /// Call from an icon's click handler. Returns true (and closes the panel) when this click should
        /// just close the panel: it's already open, or it closed a moment ago because of this same click.
        /// Clicking the bar doesn't take focus, so an open panel doesn't close itself first.
        /// </summary>
        public static bool ToggleOff(string key)
        {
            if (open != null && open.key == key && open.IsVisible) { open.Close(); return true; }
            return JustClosed(key);
        }

        public void ShowBelow(FrameworkElement anchor)
        {
            if (open != null) open.Close();
            open = this;
            var src = PresentationSource.FromVisual(anchor);
            double scale = src != null ? src.CompositionTarget.TransformToDevice.M11 : 1;
            Point p = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, anchor.ActualHeight));
            // Show off-screen first so the real size is known, then clamp inside the primary screen
            // (otherwise a flyout near the right edge spills onto the next monitor).
            Left = -20000; Top = -20000;
            Show();
            UpdateLayout();
            double w = ActualWidth;
            double screenW = Native.GetSystemMetrics(0) / scale;
            Left = Math.Max(0, Math.Min(screenW - w, p.X / scale - w / 2));
            Top = p.Y / scale + 2;
            Activate();
        }

        protected TextBlock Text(string s, double size, FontWeight weight, double alpha)
        {
            return new TextBlock { Text = s, FontSize = size, FontWeight = weight, Foreground = Theme.FgAlpha((byte)(255 * alpha)), TextTrimming = TextTrimming.CharacterEllipsis };
        }

        protected UIElement Separator()
        {
            return new Border { Height = 1, Background = Theme.FgAlpha(0x26), Margin = new Thickness(0, 8, 0, 8) };
        }

        protected UIElement Link(string label, Action onClick)
        {
            var tb = Text(label, 13, FontWeights.Normal, 1);
            var b = new Border { Child = tb, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(-6, 0, -6, 0), CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            b.MouseEnter += delegate { b.Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x64, 0xD8)); tb.Foreground = Brushes.White; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; tb.Foreground = Theme.Fg; };
            b.MouseLeftButtonUp += delegate { Close(); onClick(); };
            return b;
        }
    }

    /// <summary>Minimal macOS-style slider.</summary>
    public class MiniSlider : Grid
    {
        readonly Border fill; readonly Ellipse thumb; double value;
        public event Action<double> ValueChanged;

        public MiniSlider()
        {
            Height = 22; Background = Brushes.Transparent; Cursor = Cursors.Hand;
            Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Theme.FgAlpha(0x30), VerticalAlignment = VerticalAlignment.Center });
            fill = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Theme.Accent), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            thumb = new Ellipse { Width = 18, Height = 18, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Stroke = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), StrokeThickness = 0.5, Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.35 } };
            Children.Add(fill); Children.Add(thumb);
            SizeChanged += delegate { Layout(); };
            MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e) { CaptureMouse(); FromMouse(e.GetPosition(this)); };
            MouseMove += delegate (object s, MouseEventArgs e) { if (IsMouseCaptured) FromMouse(e.GetPosition(this)); };
            MouseLeftButtonUp += delegate { ReleaseMouseCapture(); };
            MouseWheel += delegate (object s, MouseWheelEventArgs e) { Set(value + (e.Delta > 0 ? 0.02 : -0.02), true); };
        }

        public double Value { get { return value; } set { Set(value, false); } }

        void FromMouse(Point p) { Set((p.X - 9) / Math.Max(1, ActualWidth - 18), true); }

        void Set(double v, bool notify)
        {
            value = Math.Max(0, Math.Min(1, v));
            Layout();
            if (notify && ValueChanged != null) ValueChanged(value);
        }

        void Layout()
        {
            double x = value * Math.Max(0, ActualWidth - 18);
            thumb.Margin = new Thickness(x, 0, 0, 0);
            fill.Width = x + 9;
        }
    }

    public class VolumeFlyout : Flyout
    {
        public VolumeFlyout(AudioController audio, Action onChange) : base("volume", 260)
        {
            Body.Children.Add(Text("Sound", 13, FontWeights.SemiBold, 1));
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var muteHost = new Border { Width = 26, Height = 22, Cursor = Cursors.Hand, Background = Brushes.Transparent, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0) };
            ToolTipService.SetToolTip(muteHost, "Mute / unmute");
            DockPanel.SetDock(muteHost, Dock.Left);
            var slider = new MiniSlider { Value = audio.Volume };
            var pct = Text("", 12, FontWeights.Normal, 0.7);
            pct.Width = 38; pct.TextAlignment = TextAlignment.Right; pct.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(pct, Dock.Right);
            Action refresh = delegate
            {
                muteHost.Child = Glyphs.Speaker(audio.Muted ? 0 : (audio.Volume < 0.01 ? 0 : audio.Volume < 0.34 ? 1 : audio.Volume < 0.67 ? 2 : 3), audio.Muted);
                ((FrameworkElement)muteHost.Child).VerticalAlignment = VerticalAlignment.Center;
                pct.Text = audio.Muted ? "Muted" : Math.Round(audio.Volume * 100) + "%";
            };
            refresh();
            muteHost.MouseLeftButtonUp += delegate { audio.SetMute(!audio.Muted); refresh(); onChange(); };
            slider.ValueChanged += delegate (double v) { audio.SetVolume((float)v); refresh(); onChange(); };
            row.Children.Add(muteHost); row.Children.Add(pct); row.Children.Add(slider);
            Body.Children.Add(row);
            // Output switcher (macOS-style list; the selected device gets a blue badge).
            var outputs = new StackPanel();
            Action buildOutputs = null;
            buildOutputs = delegate
            {
                outputs.Children.Clear();
                var list = audio.Outputs();
                if (list.Count == 0) return;
                var head = Text("Output", 11, FontWeights.SemiBold, 0.55);
                head.Margin = new Thickness(0, 4, 0, 2);
                outputs.Children.Add(head);
                foreach (var o in list)
                {
                    var dev = o;
                    var r = new DockPanel();
                    var badge = new Border
                    {
                        Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 10, 0),
                        Background = dev.IsDefault ? new SolidColorBrush(Theme.Accent) : Theme.FgAlpha(0x22),
                        Child = Glyphs.Speaker(2, false, dev.IsDefault ? Brushes.White : null, 14)
                    };
                    r.Children.Add(badge);
                    var name = Text(dev.Name, 13, dev.IsDefault ? FontWeights.SemiBold : FontWeights.Normal, 1);
                    name.VerticalAlignment = VerticalAlignment.Center;
                    r.Children.Add(name);
                    var host = new Border { Child = r, CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(-6, 1, -6, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand };
                    host.MouseEnter += delegate { host.Background = Theme.Hover; };
                    host.MouseLeave += delegate { host.Background = Brushes.Transparent; };
                    host.MouseLeftButtonUp += delegate
                    {
                        if (dev.IsDefault) return;
                        audio.SetDefault(dev.Id);
                        slider.Value = audio.Volume;
                        refresh(); onChange(); buildOutputs();
                    };
                    outputs.Children.Add(host);
                }
            };
            buildOutputs();
            Body.Children.Add(Separator());
            Body.Children.Add(outputs);
            Body.Children.Add(Separator());
            Body.Children.Add(Link("Sound Settings…", delegate { ShellUtil.Open("ms-settings:sound"); }));
            Body.Children.Add(Link("Volume Mixer…", delegate { ShellUtil.Open("ms-settings:apps-volume"); }));
        }
    }

    public class WeatherFlyout : Flyout
    {
        public WeatherFlyout(WeatherService ws, Action openSettings) : base("weather", 270)
        {
            var d = ws.Current;
            if (d == null) return;
            Body.Children.Add(Text(string.IsNullOrEmpty(d.Location) ? "Weather" : d.Location, 13, FontWeights.SemiBold, 1));

            var now = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            now.Children.Add(new TextBlock { Text = WeatherService.Glyph(d.Code, d.IsDay), FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI Emoji"), FontSize = 34, Foreground = Theme.Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            now.Children.Add(new TextBlock { Text = ws.Temp(d.TempC), FontSize = 36, FontWeight = FontWeights.Light, Foreground = Theme.Fg, VerticalAlignment = VerticalAlignment.Center });
            Body.Children.Add(now);

            Body.Children.Add(Text(WeatherService.Describe(d.Code), 13, FontWeights.Normal, 0.9));
            if (d.Days != null && d.Days.Count > 0)
                Body.Children.Add(Text("H:" + ws.Temp(d.Days[0].MaxC) + "  L:" + ws.Temp(d.Days[0].MinC), 12, FontWeights.Normal, 0.7));
            var extra = Text("Feels like " + ws.Temp(d.FeelsC) + " · Humidity " + Math.Round(d.Humidity) + "% · Wind " + ws.Wind(d.WindKmh), 12, FontWeights.Normal, 0.7);
            extra.TextWrapping = TextWrapping.Wrap;
            Body.Children.Add(extra);

            if (d.Days != null && d.Days.Count > 1)
            {
                Body.Children.Add(Separator());
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
                for (int i = 0; i < d.Days.Count; i++)
                {
                    var day = d.Days[i];
                    grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
                    DateTime dt;
                    string name = i == 0 ? "Today" : DateTime.TryParseExact(day.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt) ? dt.ToString("dddd", CultureInfo.CurrentCulture) : day.Date;
                    Add(grid, Text(name, 13, FontWeights.Normal, 1), i, 0, TextAlignment.Left);
                    var g = new TextBlock { Text = WeatherService.Glyph(day.Code, true), FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI Emoji"), FontSize = 15, Foreground = Theme.Fg };
                    ToolTipService.SetToolTip(g, WeatherService.Describe(day.Code));
                    Add(grid, g, i, 1, TextAlignment.Center);
                    Add(grid, Text(ws.Temp(day.MinC), 13, FontWeights.Normal, 0.6), i, 2, TextAlignment.Right);
                    Add(grid, Text(ws.Temp(day.MaxC), 13, FontWeights.Normal, 1), i, 3, TextAlignment.Right);
                }
                Body.Children.Add(grid);
            }

            Body.Children.Add(Separator());
            string when = d.FetchedUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
            var status = Text(ws.LastFetchFailed ? "Offline — showing weather from " + when : "Updated " + when, 11, FontWeights.Normal, 0.55);
            Body.Children.Add(status);
            Body.Children.Add(Text("Weather data by Open-Meteo.com", 11, FontWeights.Normal, 0.45));
            var link = Link("Change Location…", openSettings);
            ((FrameworkElement)link).Margin = new Thickness(-6, 6, -6, 0);
            Body.Children.Add(link);
        }

        static void Add(Grid g, TextBlock t, int row, int col, TextAlignment align)
        {
            t.TextAlignment = align; t.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(t, row); Grid.SetColumn(t, col);
            g.Children.Add(t);
        }
    }
}
