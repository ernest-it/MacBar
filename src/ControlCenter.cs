using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Devices.Radios;

namespace MacBar
{
    /// <summary>
    /// macOS-style Control Center: a connectivity module (Wi-Fi, Bluetooth, Airplane Mode),
    /// Focus and Screen Mirroring tiles, Display and Sound sliders, and Now Playing.
    /// </summary>
    public class ControlCenterFlyout : Flyout
    {
        readonly AudioController audio;
        readonly Action onChange;
        readonly FrameworkElement anchor;
        Radio wifi, bluetooth;
        Toggle wifiToggle, btToggle, airToggle;
        readonly Border nowPlayingCard = new Border();
        readonly DispatcherTimer mediaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };

        class Toggle
        {
            public Border Circle; public TextBlock Glyph, Title, Sub; public FrameworkElement Root; public bool On;
            public void Set(bool on, string sub)
            {
                On = on;
                Circle.Background = on ? new SolidColorBrush(Theme.Accent) : Theme.FgAlpha(0x26);
                Glyph.Foreground = on ? Brushes.White : Theme.Fg;
                if (sub != null) Sub.Text = sub;
            }
        }

        public ControlCenterFlyout(AudioController audio, FrameworkElement anchor, Action onChange) : base("control", 336)
        {
            this.audio = audio; this.anchor = anchor; this.onChange = onChange;
            Body.Margin = new Thickness(-4, -2, -4, -2);

            // Row 1: connectivity module | Focus + Screen Mirroring
            var row1 = new Grid();
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var conn = new StackPanel();
            wifiToggle = MakeToggle("", "Wi‑Fi", "…");
            btToggle = MakeToggle("", "Bluetooth", "…");
            airToggle = MakeToggle("", "Airplane Mode", "…");
            conn.Children.Add(wifiToggle.Root); conn.Children.Add(btToggle.Root); conn.Children.Add(airToggle.Root);
            var connCard = MakeCard(conn);
            connCard.Margin = new Thickness(0);
            row1.Margin = new Thickness(0, 0, 0, 8); // gap below both columns, not just the left card
            row1.Children.Add(connCard);

            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition());
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
            right.RowDefinitions.Add(new RowDefinition());
            var focus = Tile("", "Do Not Disturb", delegate { Close(); ShellUtil.Open("ms-settings:notifications"); });
            var mirror = Tile("", "Screen Mirroring", delegate { Close(); Native.PressKeys(0x5B, 0x4B); }); // Win+K
            Grid.SetRow(mirror, 2);
            right.Children.Add(focus); right.Children.Add(mirror);
            Grid.SetColumn(right, 2);
            row1.Children.Add(right);
            Body.Children.Add(row1);

            // Display brightness (only if this monitor supports it)
            var display = SliderCard("Display", "", -1, v => Brightness.Write((int)Math.Round(v * 100)));
            display.Visibility = Visibility.Collapsed;
            Body.Children.Add(display);
            ThreadPool.QueueUserWorkItem(delegate
            {
                int b = Brightness.Read();
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (b < 0) return;
                    ((MiniSlider)display.Tag).Value = b / 100.0;
                    display.Visibility = Visibility.Visible;
                }));
            });

            // Sound
            if (audio.Available)
            {
                var sound = SliderCard("Sound", "", audio.Volume, v => { audio.SetVolume((float)v); onChange(); });
                Body.Children.Add(sound);
            }

            // Now Playing
            nowPlayingCard.Visibility = Visibility.Collapsed;
            Body.Children.Add(nowPlayingCard);
            mediaTimer.Tick += delegate { RefreshMedia(); };
            mediaTimer.Start();
            Closed += delegate { mediaTimer.Stop(); };
            RefreshMedia();

            LoadRadios();
        }

        Border MakeCard(UIElement child)
        {
            return new Border
            {
                Child = child, CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 8, 10, 8),
                Background = Theme.FgAlpha(Theme.Dark ? (byte)0x14 : (byte)0x10), BorderBrush = Theme.FgAlpha(0x14), BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 8)
            };
        }

        static TextBlock GlyphText(string glyph, double size, Brush b)
        {
            var t = new TextBlock { Text = glyph, FontFamily = Glyphs.IconFont, FontSize = size, Foreground = b, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            TextOptions.SetTextFormattingMode(t, TextFormattingMode.Display);
            return t;
        }

        Toggle MakeToggle(string glyph, string title, string sub)
        {
            var t = new Toggle();
            t.Glyph = GlyphText(glyph, 14, Theme.Fg);
            t.Circle = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Child = t.Glyph, Cursor = Cursors.Hand, Background = Theme.FgAlpha(0x26), Margin = new Thickness(0, 0, 10, 0) };
            t.Title = Text(title, 13, FontWeights.SemiBold, 1);
            t.Sub = Text(sub, 11, FontWeights.Normal, 0.6);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(t.Title); texts.Children.Add(t.Sub);
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), Background = Brushes.Transparent };
            DockPanel.SetDock(t.Circle, Dock.Left);
            row.Children.Add(t.Circle); row.Children.Add(texts);
            t.Root = row;
            return t;
        }

        Border Tile(string glyph, string title, Action click)
        {
            // Icon beside the label (like macOS) keeps the two stacked tiles no taller than the connectivity card.
            var sp = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
            var circle = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Theme.FgAlpha(0x26), Child = GlyphText(glyph, 14, Theme.Fg), Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(circle, Dock.Left);
            sp.Children.Add(circle);
            var t = Text(title, 12, FontWeights.SemiBold, 1);
            t.TextWrapping = TextWrapping.Wrap;
            t.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(t);
            var card = MakeCard(sp);
            card.Margin = new Thickness(0);
            card.Cursor = Cursors.Hand;
            card.MouseEnter += delegate { card.Background = Theme.FgAlpha(0x24); };
            card.MouseLeave += delegate { card.Background = Theme.FgAlpha(Theme.Dark ? (byte)0x14 : (byte)0x10); };
            card.MouseLeftButtonUp += delegate { click(); };
            return card;
        }

        Border SliderCard(string title, string glyph, double value, Action<double> changed)
        {
            var sp = new StackPanel();
            sp.Children.Add(Text(title, 13, FontWeights.SemiBold, 1));
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 2) };
            var g = GlyphText(glyph, 14, Theme.FgAlpha(0xB0));
            g.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(g, Dock.Left);
            var slider = new MiniSlider { Value = Math.Max(0, value) };
            slider.ValueChanged += changed;
            row.Children.Add(g); row.Children.Add(slider);
            sp.Children.Add(row);
            var card = MakeCard(sp);
            card.Tag = slider;
            return card;
        }

        async void LoadRadios()
        {
            wifi = await Radios.Get(RadioKind.WiFi);
            bluetooth = await Radios.Get(RadioKind.Bluetooth);
            UpdateRadios();
            Wire(wifiToggle, async delegate (bool on) { return await Radios.Set(wifi, on); }, "ms-settings:network-wifi");
            Wire(btToggle, async delegate (bool on) { return await Radios.Set(bluetooth, on); }, "ms-settings:bluetooth");
            Wire(airToggle, delegate (bool on) { return Task.FromResult(Radios.SetAirplaneMode(on)); }, "ms-settings:network-airplanemode");
            // Clicking the Wi-Fi label opens the network list, like macOS's Wi-Fi module.
            ((DockPanel)wifiToggle.Root).Children[1].MouseLeftButtonUp += delegate
            {
                Close();
                new WifiFlyout(onChange).ShowBelow(anchor);
            };
            ((DockPanel)btToggle.Root).Children[1].MouseLeftButtonUp += delegate { Close(); ShellUtil.Open("ms-settings:bluetooth"); };
            foreach (var t in new[] { wifiToggle, btToggle }) ((FrameworkElement)((DockPanel)t.Root).Children[1]).Cursor = Cursors.Hand;
        }

        void Wire(Toggle t, Func<bool, Task<bool>> set, string fallbackUri)
        {
            t.Circle.MouseLeftButtonUp += async delegate
            {
                bool target = !t.On;
                t.Set(target, null);
                bool ok = await set(target);
                if (!ok) { ShellUtil.Open(fallbackUri); }
                await Task.Delay(600);
                UpdateRadios();
                onChange();
            };
        }

        void UpdateRadios()
        {
            if (wifi != null)
            {
                bool on = wifi.State == RadioState.On;
                var net = new NetworkState(); net.Poll();
                wifiToggle.Set(on, !on ? "Off" : net.WifiConnected ? net.Ssid : "Not connected");
            }
            else wifiToggle.Set(false, "Not available");
            if (bluetooth != null) btToggle.Set(bluetooth.State == RadioState.On, bluetooth.State == RadioState.On ? "On" : "Off");
            else btToggle.Set(false, "Not available");
            bool? air = Radios.AirplaneMode();
            airToggle.Set(air == true, air == null ? "Not available" : air.Value ? "On" : "Off");
        }

        async void RefreshMedia()
        {
            var np = await Media.Current();
            if (np == null || string.IsNullOrEmpty(np.Title)) { nowPlayingCard.Visibility = Visibility.Collapsed; return; }
            nowPlayingCard.Visibility = Visibility.Visible;
            nowPlayingCard.Child = NowPlayingView.Compact(np, delegate { RefreshMedia(); });
            nowPlayingCard.CornerRadius = new CornerRadius(14);
            nowPlayingCard.Padding = new Thickness(10);
            nowPlayingCard.Background = Theme.FgAlpha(Theme.Dark ? (byte)0x14 : (byte)0x10);
            nowPlayingCard.BorderBrush = Theme.FgAlpha(0x14);
            nowPlayingCard.BorderThickness = new Thickness(1);
        }
    }

    /// <summary>Now Playing views (shared by Control Center and future placements).</summary>
    public static class NowPlayingView
    {
        public static FrameworkElement Compact(NowPlaying np, Action refresh)
        {
            var dp = new DockPanel();
            var art = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0), Background = Theme.FgAlpha(0x22), ClipToBounds = true };
            if (np.Art != null) art.Background = new ImageBrush(np.Art) { Stretch = Stretch.UniformToFill };
            else art.Child = new TextBlock { Text = "", FontFamily = Glyphs.IconFont, FontSize = 18, Foreground = Theme.FgAlpha(0x90), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(art, Dock.Left);
            dp.Children.Add(art);

            var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            controls.Children.Add(MediaButton("", 0, refresh));
            controls.Children.Add(MediaButton(np.Playing ? "" : "", 1, refresh));
            controls.Children.Add(MediaButton("", 2, refresh));
            DockPanel.SetDock(controls, Dock.Right);
            dp.Children.Add(controls);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = np.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Theme.Fg, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(np.Artist))
                texts.Children.Add(new TextBlock { Text = np.Artist, FontSize = 12, Foreground = Theme.FgAlpha(0xA0), TextTrimming = TextTrimming.CharacterEllipsis });
            dp.Children.Add(texts);
            return dp;
        }

        static Border MediaButton(string glyph, int cmd, Action refresh)
        {
            var b = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var t = new TextBlock { Text = glyph, FontFamily = Glyphs.IconFont, FontSize = 14, Foreground = Theme.Fg, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            b.Child = t;
            b.MouseEnter += delegate { b.Background = Theme.Hover; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += async delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                await Media.Command(cmd);
                await Task.Delay(350);
                refresh();
            };
            return b;
        }
    }
}
