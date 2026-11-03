using System;
using System.Threading.Tasks;
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
    /// <summary>
    /// "Island" in the center of the menu bar: a black pill with album art, title and equalizer bars.
    /// Hover widens it to reveal playback controls; click drops down the big player card.
    /// </summary>
    public class NowPlayingIsland : Border
    {
        NowPlaying current;
        readonly Border art = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0), Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)) };
        readonly TextBlock title = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 240 };
        readonly TextBlock artist = new TextBlock { FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 160, Margin = new Thickness(6, 0, 0, 0) };
        readonly StackPanel eq = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), Height = 11 };
        readonly StackPanel controls = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, -4, 0), Visibility = Visibility.Collapsed };
        readonly TextBlock playPause;
        readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        bool busy;

        public NowPlayingIsland()
        {
            HorizontalAlignment = HorizontalAlignment.Center; VerticalAlignment = VerticalAlignment.Center;
            Height = 22; CornerRadius = new CornerRadius(11); Padding = new Thickness(4, 0, 12, 0);
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x0A, 0x0A, 0x0B));
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)); BorderThickness = new Thickness(1);
            Cursor = Cursors.Hand; Visibility = Visibility.Collapsed;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            for (int i = 0; i < 3; i++)
            {
                var bar = new Rectangle { Width = 2.5, Height = 11, RadiusX = 1.2, RadiusY = 1.2, Fill = new SolidColorBrush(Color.FromRgb(0x5C, 0xE1, 0x8B)), Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Bottom };
                bar.RenderTransformOrigin = new Point(0.5, 1);
                bar.RenderTransform = new ScaleTransform(1, 0.35);
                eq.Children.Add(bar);
            }

            controls.Children.Add(Button("", 0));
            var pp = Button("", 1);
            playPause = (TextBlock)pp.Child;
            controls.Children.Add(pp);
            controls.Children.Add(Button("", 2));

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(art); row.Children.Add(title); row.Children.Add(artist); row.Children.Add(eq); row.Children.Add(controls);
            Child = row;

            MouseEnter += delegate { controls.Visibility = Visibility.Visible; artist.Visibility = string.IsNullOrEmpty(artist.Text) ? Visibility.Collapsed : Visibility.Visible; };
            MouseLeave += delegate { controls.Visibility = Visibility.Collapsed; artist.Visibility = Visibility.Collapsed; };
            artist.Visibility = Visibility.Collapsed;
            MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                if (e.Handled || current == null) return;
                if (Flyout.ToggleOff("nowplaying")) return;
                new NowPlayingFlyout(current, delegate { Refresh(); }).ShowBelow(this);
            };
            poll.Tick += delegate { Refresh(); };
            poll.Start();
            Refresh();
        }

        Border Button(string glyph, int cmd)
        {
            var t = new TextBlock { Text = glyph, FontFamily = Glyphs.IconFont, FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var b = new Border { Width = 22, Height = 20, CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, Child = t };
            b.MouseEnter += delegate { b.Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)); };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += async delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                await Media.Command(cmd);
                await Task.Delay(300);
                Refresh();
            };
            return b;
        }

        public async void Refresh()
        {
            if (busy) return;
            busy = true;
            try
            {
                var np = await Media.Current(current);
                if (np == null || string.IsNullOrEmpty(np.Title)) { Visibility = Visibility.Collapsed; current = null; return; }
                bool changed = current == null || !np.SameTrack(current) || np.Playing != current.Playing || np.Art != current.Art;
                current = np;
                Visibility = Visibility.Visible;
                if (!changed) return;
                title.Text = np.Title;
                artist.Text = np.Artist ?? "";
                art.Background = np.Art != null ? (Brush)new ImageBrush(np.Art) { Stretch = Stretch.UniformToFill } : new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C));
                playPause.Text = np.Playing ? "" : "";
                Animate(np.Playing);
                ToolTip = np.Title + (string.IsNullOrEmpty(np.Artist) ? "" : " — " + np.Artist) + (string.IsNullOrEmpty(np.AppName) ? "" : "\n" + np.AppName);
            }
            finally { busy = false; }
        }

        void Animate(bool playing)
        {
            double[] periods = { 0.42, 0.55, 0.48 };
            for (int i = 0; i < eq.Children.Count; i++)
            {
                var st = (ScaleTransform)((Rectangle)eq.Children[i]).RenderTransform;
                if (playing)
                {
                    var a = new DoubleAnimation(0.25, 1.0, TimeSpan.FromSeconds(periods[i]))
                    {
                        AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                        BeginTime = TimeSpan.FromMilliseconds(i * 120)
                    };
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
                }
                else
                {
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    st.ScaleY = 0.3;
                }
            }
        }
    }

    /// <summary>Big player card with a blurred album-art backdrop, scrubbable progress and transport controls.</summary>
    public class NowPlayingFlyout : Flyout
    {
        NowPlaying np;
        readonly Action onChange;
        readonly Border progressFill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left };
        readonly Grid progress = new Grid { Height = 14, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        readonly TextBlock elapsed, remaining, titleText, artistText, appText;
        readonly TextBlock playGlyph;
        readonly Border bigArt, backdrop;
        readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        int pollCount;

        static SolidColorBrush W(byte a) { return new SolidColorBrush(Color.FromArgb(a, 0xFF, 0xFF, 0xFF)); }

        public NowPlayingFlyout(NowPlaying first, Action onChange) : base("nowplaying", 340)
        {
            np = first; this.onChange = onChange;

            // Backdrop: the album art, heavily blurred and darkened, clipped to the card's rounded shape.
            Card.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
            Card.BorderBrush = W(0x26);
            Card.Padding = new Thickness(0);
            var layers = new Grid();
            backdrop = new Border { Margin = new Thickness(-40), Effect = new BlurEffect { Radius = 60, KernelType = KernelType.Gaussian } };
            layers.Children.Add(backdrop);
            layers.Children.Add(new Border { Background = new LinearGradientBrush(Color.FromArgb(0x70, 0, 0, 0), Color.FromArgb(0xB8, 0, 0, 0), 90) });
            Card.Child = null;
            layers.Children.Add(Body);
            Body.Margin = new Thickness(16, 16, 16, 14);
            Card.Child = layers;
            layers.SizeChanged += delegate { layers.Clip = new RectangleGeometry(new Rect(0, 0, layers.ActualWidth, layers.ActualHeight), 11, 11); };

            var top = new DockPanel();
            bigArt = new Border { Width = 84, Height = 84, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 14, 0), Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.5 } };
            DockPanel.SetDock(bigArt, Dock.Left);
            top.Children.Add(bigArt);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleText = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxHeight = 44, TextTrimming = TextTrimming.CharacterEllipsis, Cursor = Cursors.Hand };
            artistText = new TextBlock { FontSize = 13, Foreground = W(0xC0), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            appText = new TextBlock { FontSize = 11, Foreground = W(0x80), Margin = new Thickness(0, 6, 0, 0) };
            texts.Children.Add(titleText); texts.Children.Add(artistText); texts.Children.Add(appText);
            top.Children.Add(texts);
            Body.Children.Add(top);
            titleText.MouseLeftButtonUp += delegate { OpenApp(); };

            // Progress
            progress.Margin = new Thickness(0, 16, 0, 0);
            progress.Children.Add(new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = W(0x40) });
            progress.Children.Add(progressFill);
            progress.MouseLeftButtonUp += async delegate (object s, MouseButtonEventArgs e)
            {
                if (np == null || !np.CanSeek || np.Duration <= 0) return;
                double frac = Math.Max(0, Math.Min(1, e.GetPosition(progress).X / progress.ActualWidth));
                await Media.Seek(frac * np.Duration);
                np.Position = frac * np.Duration; np.PositionAt = DateTime.UtcNow;
                UpdateProgress();
            };
            Body.Children.Add(progress);
            var times = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            elapsed = new TextBlock { FontSize = 11, Foreground = W(0x99) };
            remaining = new TextBlock { FontSize = 11, Foreground = W(0x99), HorizontalAlignment = HorizontalAlignment.Right };
            DockPanel.SetDock(elapsed, Dock.Left);
            times.Children.Add(elapsed); times.Children.Add(remaining);
            Body.Children.Add(times);

            // Transport
            var transport = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            transport.Children.Add(Transport("", 0, 36, 16));
            var play = Transport("", 1, 48, 22);
            playGlyph = (TextBlock)play.Child;
            play.Margin = new Thickness(18, 0, 18, 0);
            transport.Children.Add(play);
            transport.Children.Add(Transport("", 2, 36, 16));
            Body.Children.Add(transport);

            Show(np);
            tick.Tick += async delegate
            {
                UpdateProgress();
                if (++pollCount % 3 == 0)
                {
                    var fresh = await Media.Current(np);
                    if (fresh == null) { Close(); return; }
                    bool trackChanged = !fresh.SameTrack(np);
                    np = fresh;
                    if (trackChanged) Show(np); else { playGlyph.Text = np.Playing ? "" : ""; UpdateProgress(); }
                }
            };
            tick.Start();
            Closed += delegate { tick.Stop(); };
        }

        Border Transport(string glyph, int cmd, double size, double font)
        {
            var t = new TextBlock { Text = glyph, FontFamily = Glyphs.IconFont, FontSize = font, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = cmd == 1 ? W(0x26) : Brushes.Transparent, Child = t, Cursor = Cursors.Hand };
            var normal = b.Background;
            b.MouseEnter += delegate { b.Background = W(0x38); };
            b.MouseLeave += delegate { b.Background = normal; };
            b.MouseLeftButtonUp += async delegate
            {
                if (cmd == 1 && np != null) { np.Playing = !np.Playing; t.Text = np.Playing ? "" : ""; }
                await Media.Command(cmd);
                await Task.Delay(400);
                var fresh = await Media.Current(np);
                if (fresh != null) { bool changed = !fresh.SameTrack(np); np = fresh; if (changed) Show(np); else UpdateProgress(); playGlyph.Text = np.Playing ? "" : ""; }
                onChange();
            };
            return b;
        }

        void Show(NowPlaying n)
        {
            titleText.Text = n.Title;
            artistText.Text = n.Artist ?? "";
            appText.Text = string.IsNullOrEmpty(n.AppName) ? "" : "Playing from " + n.AppName;
            if (n.Art != null)
            {
                bigArt.Background = new ImageBrush(n.Art) { Stretch = Stretch.UniformToFill };
                bigArt.Child = null;
                backdrop.Background = new ImageBrush(n.Art) { Stretch = Stretch.UniformToFill };
            }
            else
            {
                bigArt.Background = W(0x22);
                bigArt.Child = new TextBlock { Text = "", FontFamily = Glyphs.IconFont, FontSize = 30, Foreground = W(0xB0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                backdrop.Background = new LinearGradientBrush(Color.FromRgb(0x3A, 0x2A, 0x5E), Color.FromRgb(0x14, 0x3E, 0x5C), 45);
            }
            playGlyph.Text = n.Playing ? "" : "";
            UpdateProgress();
        }

        static string Fmt(double s)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, s));
            return t.TotalHours >= 1 ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds) : string.Format("{0}:{1:00}", t.Minutes, t.Seconds);
        }

        void UpdateProgress()
        {
            if (np == null || np.Duration <= 0)
            {
                progress.Visibility = Visibility.Collapsed; elapsed.Text = ""; remaining.Text = "";
                return;
            }
            progress.Visibility = Visibility.Visible;
            double pos = np.LivePosition;
            progressFill.Width = Math.Max(4, progress.ActualWidth * pos / np.Duration);
            elapsed.Text = Fmt(pos);
            remaining.Text = "-" + Fmt(np.Duration - pos);
        }

        void OpenApp()
        {
            if (np == null || string.IsNullOrEmpty(np.AppId)) return;
            Close();
            ShellUtil.Open(np.AppId.Contains("!") ? "shell:AppsFolder\\" + np.AppId : "shell:AppsFolder\\" + np.AppId);
        }
    }
}
