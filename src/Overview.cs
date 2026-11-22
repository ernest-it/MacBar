using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MacBar
{
    /// <summary>
    /// Window overview (one tap of the Windows key, like GNOME's Activities): every open window as a live
    /// thumbnail, laid out in a grid between the menu bar and the dock. The thumbnails are DWM thumbnails,
    /// so they stay live (video keeps playing). Click one to switch to it; middle-click closes it.
    /// </summary>
    public class OverviewWindow : Window
    {
        [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int L, R, T, B; }
        [StructLayout(LayoutKind.Sequential)]
        struct ThumbProps
        {
            public uint Flags; public RECT Dest; public RECT Source; public byte Opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool Visible;
            [MarshalAs(UnmanagedType.Bool)] public bool ClientOnly;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPLACEMENT { public int Length, Flags, ShowCmd; public POINT MinPos, MaxPos; public RECT Normal; }

        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
        [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref ThumbProps p);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);

        const uint TNP_DEST = 1, TNP_SOURCE = 2, TNP_OPACITY = 4, TNP_VISIBLE = 8, TNP_CLIENTONLY = 0x10;

        class Tile
        {
            public IntPtr Hwnd, Thumb;
            public bool Minimized;
            public Rect From, To;   // physical pixels in this window (= screen, it covers the primary monitor from 0,0)
            public RECT Source;     // the visible frame inside the source window (no invisible resize borders)
            public Border Frame;
            public ImageSource Icon;
        }

        readonly Grid root = new Grid();
        readonly Canvas canvas = new Canvas();
        readonly List<Tile> tiles = new List<Tile>();
        IntPtr hwnd;
        double scale = 1, progress;
        int cols = 1, selected = -1;
        bool closing;
        EventHandler tick;

        /// <summary>Typing in the overview hands the text to the app page's search (like GNOME).</summary>
        public Action<string> TypeToSearch;
        public bool IsClosing { get { return closing; } }

        public OverviewWindow()
        {
            // Same setup as the app page: not layered (DWM thumbnails don't draw on layered windows),
            // with DWM's acrylic blur behind.
            WindowStyle = WindowStyle.None; AllowsTransparency = false; Background = Brushes.Transparent;
            Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
            FontFamily = Theme.Font; UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            root.Background = new SolidColorBrush(Color.FromArgb(0x55, 0x08, 0x08, 0x0C)); // hit-testable: a click on empty space closes
            root.Children.Add(canvas);
            Content = root;

            root.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e) { if (!e.Handled) Dismiss(IntPtr.Zero); };
            PreviewKeyDown += OnKey;
            TextInput += delegate (object s, TextCompositionEventArgs e)
            {
                if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]) || e.Text == " ") return;
                e.Handled = true;
                if (TypeToSearch != null) TypeToSearch(e.Text); else DismissNow();
            };
            Deactivated += delegate { if (IsVisible && !closing) Dismiss(IntPtr.Zero); };
            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW);
                HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                var m = new MARGINS { L = -1, R = -1, T = -1, B = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref m);
                Native.SetAccent(hwnd, 4, 0x40100C0C);
            };
        }

        public void Open()
        {
            StopAnim();
            Clear();
            closing = false;
            var src = PresentationSource.FromVisual(App.Instance.MainBar);
            scale = src != null ? src.CompositionTarget.TransformToDevice.M11 : 1;
            int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
            Left = 0; Top = 0; Width = sw / scale; Height = sh / scale;
            root.Opacity = 0;
            Show();
            Activate();
            if (Native.GetForegroundWindow() != hwnd)
            {
                // Windows' focus lock can refuse; tapping Alt gets past it (as when the dock switches windows).
                Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
                Native.SetForegroundWindow(hwnd);
                Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
            }
            Focus();
            Build(sw, sh);
            Animate(1, 230, null);
        }

        /// <summary>Animates the thumbnails back to their windows, then switches to <paramref name="target"/> (if any).</summary>
        public void Dismiss(IntPtr target)
        {
            if (closing || !IsVisible) return;
            closing = true;
            Animate(0, 180, delegate
            {
                // Switch while still covering the screen, so the old window doesn't flash up first.
                if (target != IntPtr.Zero) WindowTracker.Activate(target);
                Clear();
                Hide();
                closing = false;
            });
        }

        /// <summary>Closes at once (going to the app page).</summary>
        public void DismissNow()
        {
            StopAnim();
            Clear();
            if (IsVisible) Hide();
            closing = false;
        }

        // ---------------------------------------------------------------- layout

        void Build(int sw, int sh)
        {
            var wins = WindowTracker.GetAppWindows().Where(w => !w.IsUac && Native.IsWindow(w.Hwnd)).ToList();
            var dock = App.Instance.S;
            double top = (App.Instance.MainBar.ActualHeight + 36) * scale;
            double bottom = sh - (dock.IconSize + 58) * scale;
            double side = 56 * scale, gap = 32 * scale, labelH = 34 * scale;
            double areaW = sw - side * 2, areaH = bottom - top;

            if (wins.Count == 0)
            {
                var none = new TextBlock { Text = "No open windows", FontSize = 18, Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)) };
                none.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(none, (sw / scale - none.DesiredSize.Width) / 2);
                Canvas.SetTop(none, (top + areaH / 2) / scale - none.DesiredSize.Height / 2);
                canvas.Children.Add(none);
                return;
            }

            // Real positions (physical pixels whatever each window's DPI), so thumbnails start on their windows.
            var sizes = new List<Size>();
            IntPtr old = IntPtr.Zero;
            try { old = SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch { }
            try
            {
                foreach (var w in wins)
                {
                    var t = new Tile { Hwnd = w.Hwnd, Minimized = Native.IsIconic(w.Hwnd), Icon = IconFor(w) };
                    RECT wr, fr;
                    Native.GetWindowRect(w.Hwnd, out wr);
                    if (DwmGetWindowAttribute(w.Hwnd, 9, out fr, Marshal.SizeOf(typeof(RECT))) != 0) fr = wr;
                    if (t.Minimized)
                    {
                        var p = new WINDOWPLACEMENT { Length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
                        GetWindowPlacement(w.Hwnd, ref p);
                        fr = p.Normal;
                    }
                    double fw = Math.Max(120, fr.Right - fr.Left), fh = Math.Max(80, fr.Bottom - fr.Top);
                    t.From = new Rect(fr.Left, fr.Top, fw, fh);
                    t.Source = new RECT { Left = fr.Left - wr.Left, Top = fr.Top - wr.Top, Right = fr.Right - wr.Left, Bottom = fr.Bottom - wr.Top };
                    tiles.Add(t);
                    sizes.Add(new Size(fw, fh));
                }
            }
            finally { if (old != IntPtr.Zero) try { SetThreadDpiAwarenessContext(old); } catch { } }

            // Pick the row count that shows the windows biggest.
            int n = tiles.Count, bestRows = 1;
            double bestArea = -1, maxScale = n == 1 ? 0.7 : 0.6;
            for (int rows = 1; rows <= n; rows++)
            {
                int c = (int)Math.Ceiling(n / (double)rows);
                double cw = (areaW - gap * (c - 1)) / c, ch = (areaH - gap * (rows - 1)) / rows - labelH;
                if (cw <= 0 || ch <= 0) continue;
                double area = 0;
                foreach (var s in sizes) { double k = Math.Min(maxScale, Math.Min(cw / s.Width, ch / s.Height)); area += s.Width * s.Height * k * k; }
                if (area > bestArea) { bestArea = area; bestRows = rows; }
            }
            cols = (int)Math.Ceiling(n / (double)bestRows);
            double cellW = (areaW - gap * (cols - 1)) / cols, cellH = (areaH - gap * (bestRows - 1)) / bestRows - labelH;
            // Shrink the cells to what the windows actually use, so a few small windows sit together in the middle.
            double usedH = 0;
            foreach (var s in sizes) usedH = Math.Max(usedH, s.Height * Math.Min(maxScale, Math.Min(cellW / s.Width, cellH / s.Height)));
            cellH = Math.Min(cellH, usedH);
            double gridH = bestRows * (cellH + labelH) + (bestRows - 1) * gap;
            double y0 = top + (areaH - gridH) / 2;

            for (int i = 0; i < n; i++)
            {
                int row = i / cols, col = i % cols;
                int inRow = Math.Min(cols, n - row * cols);
                double rowW = inRow * cellW + (inRow - 1) * gap;
                double cx = side + (areaW - rowW) / 2 + col * (cellW + gap), cy = y0 + row * (cellH + labelH + gap);
                var t = tiles[i];
                var s = sizes[i];
                double k = Math.Min(maxScale, Math.Min(cellW / s.Width, cellH / s.Height));
                double tw = Math.Round(s.Width * k), th = Math.Round(s.Height * k);
                t.To = new Rect(Math.Round(cx + (cellW - tw) / 2), Math.Round(cy + cellH - th), tw, th);
                if (t.Minimized) t.From = t.To; // nothing on screen to fly out of
                AddChrome(t, i, cx, cellW);
                if (!t.Minimized && DwmRegisterThumbnail(hwnd, t.Hwnd, out t.Thumb) != 0) t.Thumb = IntPtr.Zero;
            }
            Apply(0);
        }

        /// <summary>Hover ring, title and icon (and a card for minimized windows, which have no live picture).</summary>
        void AddChrome(Tile t, int index, double cellX, double cellW)
        {
            double pad = 6;
            var r = t.To;
            var frame = new Border
            {
                Width = r.Width / scale + pad * 2, Height = r.Height / scale + pad * 2,
                CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(3),
                BorderBrush = Brushes.Transparent, Background = Brushes.Transparent, Cursor = Cursors.Hand
            };
            if (t.Minimized)
            {
                var card = new Border
                {
                    Margin = new Thickness(pad - 3), CornerRadius = new CornerRadius(8),
                    Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1)
                };
                var icon = t.Icon;
                double sz = Math.Min(64, Math.Min(r.Width, r.Height) / scale * 0.5);
                card.Child = new Image { Source = icon, Width = sz, Height = sz, Opacity = 0.85 };
                frame.Child = card;
            }
            Canvas.SetLeft(frame, r.X / scale - pad);
            Canvas.SetTop(frame, r.Y / scale - pad);
            t.Frame = frame;

            var label = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var img = t.Icon;
            if (img != null) label.Children.Add(new Image { Source = img, Width = 18, Height = 18, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            label.Children.Add(new TextBlock
            {
                Text = Native.GetWindowTitle(t.Hwnd), FontSize = 13, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = Math.Max(60, cellW / scale - 30), Effect = Theme.Halo()
            });
            var holder = new Grid { Width = cellW / scale, IsHitTestVisible = false };
            holder.Children.Add(label);
            Canvas.SetLeft(holder, cellX / scale);
            Canvas.SetTop(holder, (r.Bottom) / scale + 10);

            frame.MouseEnter += delegate { Select(index); };
            frame.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; Dismiss(t.Hwnd); };
            frame.MouseUp += delegate (object s, MouseButtonEventArgs e)
            {
                if (e.ChangedButton != MouseButton.Middle) return;
                e.Handled = true;
                Native.PostMessage(t.Hwnd, 0x10 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                frame.Opacity = 0.4;
                // Lay out again once it's gone (an app that asks "save changes?" keeps its window).
                var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                later.Tick += delegate
                {
                    later.Stop();
                    if (!IsVisible || closing) return;
                    if (Native.IsWindow(t.Hwnd) && Native.IsWindowVisible(t.Hwnd)) { frame.Opacity = 1; return; }
                    Clear();
                    Build(Native.GetSystemMetrics(0), Native.GetSystemMetrics(1));
                    Apply(1);
                };
                later.Start();
            };
            canvas.Children.Add(frame);
            canvas.Children.Add(holder);
        }

        /// <summary>The app's own icon (as in the dock), so Store apps like Settings get theirs too.</summary>
        ImageSource IconFor(AppWindow w)
        {
            int px = (int)Math.Round(64 * scale);
            string src = w.IsUwp && !string.IsNullOrEmpty(w.Aumid) ? "shell:AppsFolder\\" + w.Aumid : w.ExePath;
            ImageSource icon = null;
            try { if (src != null) icon = IconLoader.Load(src, px); } catch { }
            return icon ?? IconLoader.ForWindow(w.Hwnd);
        }

        void Select(int i)
        {
            selected = i;
            for (int j = 0; j < tiles.Count; j++)
                tiles[j].Frame.BorderBrush = j == i ? new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            int n = tiles.Count;
            int move = 0;
            if (e.Key == Key.Escape) { Dismiss(IntPtr.Zero); e.Handled = true; return; }
            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                if (selected >= 0 && selected < n) Dismiss(tiles[selected].Hwnd);
                e.Handled = true; return;
            }
            if (e.Key == Key.Right || (e.Key == Key.Tab && Keyboard.Modifiers != ModifierKeys.Shift)) move = 1;
            else if (e.Key == Key.Left || e.Key == Key.Tab) move = -1;
            else if (e.Key == Key.Down) move = cols;
            else if (e.Key == Key.Up) move = -cols;
            if (move == 0 || n == 0) return;
            e.Handled = true;
            int next = selected < 0 ? 0 : selected + move;
            if (e.Key == Key.Tab || e.Key == Key.Left || e.Key == Key.Right) next = (next % n + n) % n;
            if (next >= 0 && next < n) Select(next);
        }

        // ---------------------------------------------------------------- animation

        static double Lerp(double a, double b, double t) { return a + (b - a) * t; }

        /// <summary>Places every thumbnail <paramref name="t"/> of the way from its window (0) to its grid spot (1).</summary>
        void Apply(double t)
        {
            progress = t;
            root.Opacity = t;
            foreach (var tile in tiles)
            {
                if (tile.Thumb == IntPtr.Zero) continue;
                var p = new ThumbProps
                {
                    Flags = TNP_DEST | TNP_SOURCE | TNP_OPACITY | TNP_VISIBLE | TNP_CLIENTONLY,
                    Dest = new RECT
                    {
                        Left = (int)Math.Round(Lerp(tile.From.Left, tile.To.Left, t)), Top = (int)Math.Round(Lerp(tile.From.Top, tile.To.Top, t)),
                        Right = (int)Math.Round(Lerp(tile.From.Right, tile.To.Right, t)), Bottom = (int)Math.Round(Lerp(tile.From.Bottom, tile.To.Bottom, t))
                    },
                    Source = tile.Source, Opacity = 255, Visible = true, ClientOnly = false
                };
                DwmUpdateThumbnailProperties(tile.Thumb, ref p);
            }
        }

        void Animate(double to, double ms, Action done)
        {
            StopAnim();
            double from = progress;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            tick = delegate
            {
                double k = Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
                Apply(from + (to - from) * (1 - Math.Pow(1 - k, 3)));
                if (k < 1) return;
                StopAnim();
                if (done != null) done();
            };
            CompositionTarget.Rendering += tick;
        }

        void StopAnim()
        {
            if (tick != null) { CompositionTarget.Rendering -= tick; tick = null; }
        }

        void Clear()
        {
            foreach (var t in tiles) if (t.Thumb != IntPtr.Zero) DwmUnregisterThumbnail(t.Thumb);
            tiles.Clear();
            canvas.Children.Clear();
            selected = -1;
        }
    }
}
