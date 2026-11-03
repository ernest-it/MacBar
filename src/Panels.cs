using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MacBar
{
    /// <summary>macOS-style on/off switch.</summary>
    public class ToggleSwitch : Border
    {
        readonly Ellipse knob = new Ellipse { Width = 18, Height = 18, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.3 } };
        bool on;
        public event Action<bool> Toggled;

        public ToggleSwitch(bool initial)
        {
            Width = 38; Height = 22; CornerRadius = new CornerRadius(11); Padding = new Thickness(2); Cursor = Cursors.Hand;
            VerticalAlignment = VerticalAlignment.Center;
            Child = knob;
            on = initial; Render(false);
            MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; IsOn = !on; if (Toggled != null) Toggled(on); };
        }

        public bool IsOn { get { return on; } set { on = value; Render(true); } }

        void Render(bool animate)
        {
            Background = on ? new SolidColorBrush(Theme.Accent) : Theme.FgAlpha(0x40);
            var to = new Thickness(on ? 16 : 0, 0, 0, 0);
            if (animate) knob.BeginAnimation(MarginProperty, new ThicknessAnimation(to, TimeSpan.FromMilliseconds(140)));
            else knob.Margin = to;
        }
    }

    /// <summary>Wi-Fi panel inspired by macOS Control Center / GNOME quick settings.</summary>
    public class WifiFlyout : Flyout
    {
        readonly StackPanel list = new StackPanel();
        readonly ToggleSwitch toggle;
        readonly Action onChange;
        readonly DispatcherTimer refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };

        public WifiFlyout(Action onChange) : base("wifi", 300)
        {
            this.onChange = onChange;
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            toggle = new ToggleSwitch(WifiControl.RadioOn());
            DockPanel.SetDock(toggle, Dock.Right);
            head.Children.Add(toggle);
            var title = Text("Wi‑Fi", 14, FontWeights.SemiBold, 1);
            title.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(title);
            Body.Children.Add(head);
            Body.Children.Add(list);
            Body.Children.Add(Separator());
            Body.Children.Add(Link("Wi‑Fi Settings…", delegate { ShellUtil.Open("ms-settings:network-wifi"); }));
            Body.Children.Add(Link("Network & Internet Settings…", delegate { ShellUtil.Open("ms-settings:network"); }));

            toggle.Toggled += delegate (bool on)
            {
                if (!WifiControl.SetRadio(on))
                {
                    toggle.IsOn = !on;
                    ShellUtil.Open("ms-settings:network-wifi"); // Windows refused; let the user flip it there
                    return;
                }
                Populate();
                onChange();
            };
            refresh.Tick += delegate { Populate(); onChange(); };
            refresh.Start();
            Closed += delegate { refresh.Stop(); };
            Populate();
            WifiControl.Scan();
        }

        void Section(string text)
        {
            var t = Text(text, 11, FontWeights.SemiBold, 0.55);
            t.Margin = new Thickness(0, 8, 0, 2);
            list.Children.Add(t);
        }

        void Populate()
        {
            list.Children.Clear();
            if (!toggle.IsOn)
            {
                var off = Text("Wi‑Fi is turned off.", 13, FontWeights.Normal, 0.6);
                off.Margin = new Thickness(0, 4, 0, 4);
                list.Children.Add(off);
                return;
            }
            var nets = WifiControl.Networks();
            if (nets.Count == 0) { list.Children.Add(Text("Searching for networks…", 13, FontWeights.Normal, 0.6)); return; }
            foreach (var n in nets.Where(x => x.Connected)) list.Children.Add(Row(n));
            var known = nets.Where(x => !x.Connected && x.HasProfile).ToList();
            if (known.Count > 0) { Section("Known Networks"); foreach (var n in known) list.Children.Add(Row(n)); }
            var other = nets.Where(x => !x.Connected && !x.HasProfile).Take(10).ToList();
            if (other.Count > 0) { Section("Other Networks"); foreach (var n in other) list.Children.Add(Row(n)); }
        }

        UIElement Row(WifiNetwork n)
        {
            var row = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(-6, 1, -6, 1) };
            var host = new Border { Child = row, CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 4, 6, 4), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            int bars = n.Signal >= 67 ? 3 : n.Signal >= 34 ? 2 : 1;
            var badge = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 10, 0),
                Background = n.Connected ? new SolidColorBrush(Theme.Accent) : Theme.FgAlpha(0x22),
                Child = new Viewbox { Width = 14, Height = 12, Child = Glyphs.Wifi(bars, true, n.Connected ? Brushes.White : null) }
            };
            DockPanel.SetDock(badge, Dock.Left);
            row.Children.Add(badge);

            if (n.Secured)
            {
                var lockGlyph = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Foreground = Theme.FgAlpha(0x90), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
                DockPanel.SetDock(lockGlyph, Dock.Right);
                row.Children.Add(lockGlyph);
            }
            if (n.Connected)
            {
                var disc = Text("Disconnect", 11, FontWeights.Normal, 0.7);
                disc.VerticalAlignment = VerticalAlignment.Center;
                disc.TextDecorations = TextDecorations.Underline;
                disc.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; WifiControl.Disconnect(); Populate(); onChange(); };
                DockPanel.SetDock(disc, Dock.Right);
                row.Children.Add(disc);
            }
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(Text(n.Ssid, 13, n.Connected ? FontWeights.SemiBold : FontWeights.Normal, 1));
            if (n.Connected) names.Children.Add(Text("Connected", 11, FontWeights.Normal, 0.6));
            row.Children.Add(names);

            host.MouseEnter += delegate { host.Background = Theme.Hover; };
            host.MouseLeave += delegate { host.Background = Brushes.Transparent; };
            host.MouseLeftButtonUp += delegate
            {
                if (n.Connected) return;
                if (WifiControl.Connect(n))
                {
                    list.Children.Clear();
                    list.Children.Add(Text("Connecting to " + n.Ssid + "…", 13, FontWeights.Normal, 0.7));
                }
                else
                {
                    Close();
                    ShellUtil.Open("ms-availablenetworks:"); // new network: Windows asks for the password
                }
            };
            return host;
        }
    }

    /// <summary>Clock + month calendar + notifications, like GNOME's date menu and macOS Notification Center.</summary>
    public class CalendarFlyout : Flyout
    {
        DateTime month;
        readonly Grid grid = new Grid();
        readonly TextBlock monthLabel;

        public CalendarFlyout() : base("calendar", 320)
        {
            var now = DateTime.Now;
            var ci = CultureInfo.CurrentCulture;
            month = new DateTime(now.Year, now.Month, 1);

            Body.Children.Add(Text(now.ToString("dddd", ci), 13, FontWeights.SemiBold, 0.6));
            Body.Children.Add(Text(now.ToString(ci.DateTimeFormat.LongDatePattern.Replace("dddd, ", "").Replace("dddd ", ""), ci), 20, FontWeights.SemiBold, 1));

            var nav = new DockPanel { Margin = new Thickness(0, 12, 0, 4) };
            var next = NavButton(""); var prev = NavButton("");
            var today = Text("Today", 12, FontWeights.SemiBold, 0.8);
            today.Cursor = Cursors.Hand; today.VerticalAlignment = VerticalAlignment.Center; today.Margin = new Thickness(6, 0, 6, 0);
            DockPanel.SetDock(next, Dock.Right); DockPanel.SetDock(today, Dock.Right); DockPanel.SetDock(prev, Dock.Right);
            nav.Children.Add(next); nav.Children.Add(today); nav.Children.Add(prev);
            monthLabel = Text("", 14, FontWeights.SemiBold, 1);
            monthLabel.VerticalAlignment = VerticalAlignment.Center;
            nav.Children.Add(monthLabel);
            prev.MouseLeftButtonUp += delegate { month = month.AddMonths(-1); BuildMonth(); };
            next.MouseLeftButtonUp += delegate { month = month.AddMonths(1); BuildMonth(); };
            today.MouseLeftButtonUp += delegate { month = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1); BuildMonth(); };
            Body.Children.Add(nav);
            for (int i = 0; i < 7; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            Body.Children.Add(grid);
            BuildMonth();

            Body.Children.Add(Separator());
            Body.Children.Add(Text("Notifications", 13, FontWeights.SemiBold, 1));
            var card = new Border { CornerRadius = new CornerRadius(10), Background = Theme.FgAlpha(0x14), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 6, 0, 0) };
            var cardBody = new StackPanel();
            var info = Text("Your notifications live in Windows' Notification Center.", 12, FontWeights.Normal, 0.7);
            info.TextWrapping = TextWrapping.Wrap;
            cardBody.Children.Add(info);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(Pill("Show Notifications", true, delegate { Close(); Native.PressKeys(0x5B, 0x4E); }));
            buttons.Children.Add(Pill("Do Not Disturb…", false, delegate { Close(); ShellUtil.Open("ms-settings:notifications"); }));
            cardBody.Children.Add(buttons);
            card.Child = cardBody;
            Body.Children.Add(card);

            Body.Children.Add(Separator());
            Body.Children.Add(Link("Date & Time Settings…", delegate { ShellUtil.Open("ms-settings:dateandtime"); }));
        }

        Border NavButton(string glyph)
        {
            var b = new Border { Width = 26, Height = 24, CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            b.Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Foreground = Theme.Fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            b.MouseEnter += delegate { b.Background = Theme.Hover; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            return b;
        }

        Border Pill(string text, bool primary, Action click)
        {
            var t = new TextBlock { Text = text, FontSize = 12, Foreground = primary ? Brushes.White : Theme.Fg };
            var b = new Border
            {
                Child = t, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 4, 12, 5), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand,
                Background = primary ? new SolidColorBrush(Theme.Accent) : Theme.FgAlpha(0x22)
            };
            b.MouseEnter += delegate { b.Opacity = 0.85; };
            b.MouseLeave += delegate { b.Opacity = 1; };
            b.MouseLeftButtonUp += delegate { click(); };
            return b;
        }

        void BuildMonth()
        {
            var ci = CultureInfo.CurrentCulture;
            monthLabel.Text = month.ToString(ci.DateTimeFormat.YearMonthPattern, ci);
            grid.Children.Clear(); grid.RowDefinitions.Clear();
            int first = (int)ci.DateTimeFormat.FirstDayOfWeek;
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
            for (int c = 0; c < 7; c++)
            {
                var h = Text(ci.DateTimeFormat.ShortestDayNames[(first + c) % 7], 11, FontWeights.SemiBold, 0.5);
                h.HorizontalAlignment = HorizontalAlignment.Center; h.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(h, c); grid.Children.Add(h);
            }
            int offset = ((int)month.DayOfWeek - first + 7) % 7;
            DateTime start = month.AddDays(-offset);
            DateTime todayDate = DateTime.Today;
            for (int r = 0; r < 6; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
                for (int c = 0; c < 7; c++)
                {
                    DateTime d = start.AddDays(r * 7 + c);
                    bool inMonth = d.Month == month.Month, isToday = d == todayDate;
                    bool weekend = d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday;
                    var t = new TextBlock
                    {
                        Text = d.Day.ToString(ci), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                        FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                        Foreground = isToday ? Brushes.White : Theme.FgAlpha((byte)(!inMonth ? 0x55 : weekend ? 0xB0 : 0xFF))
                    };
                    var cell = new Border
                    {
                        Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Child = t,
                        Background = isToday ? new SolidColorBrush(Theme.Accent) : Brushes.Transparent
                    };
                    if (!isToday)
                    {
                        cell.MouseEnter += delegate { cell.Background = Theme.Hover; };
                        cell.MouseLeave += delegate { cell.Background = Brushes.Transparent; };
                    }
                    Grid.SetRow(cell, r + 1); Grid.SetColumn(cell, c);
                    grid.Children.Add(cell);
                }
            }
        }
    }
}
