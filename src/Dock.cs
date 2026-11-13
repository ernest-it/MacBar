using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MacBar
{
    public class DockItem
    {
        public PinnedApp Pin;
        public string Aumid, ExePath, Name, LaunchTarget;
        public bool IsUwp;
        public readonly List<AppWindow> Windows = new List<AppWindow>();
        public FrameworkElement View;
        public Ellipse Dot;
        public Image Img;
        public ScaleTransform Scale;
        public TranslateTransform Bounce;

        public bool Matches(AppWindow w)
        {
            // Windows 11's File Explorer pin only has an AUMID, while Explorer windows usually have none.
            if (string.Equals(Aumid, "Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase) &&
                w.ExePath != null && w.ExePath.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(Aumid) && !string.IsNullOrEmpty(w.Aumid))
                return string.Equals(Aumid, w.Aumid, StringComparison.OrdinalIgnoreCase);
            return !string.IsNullOrEmpty(ExePath) && !string.IsNullOrEmpty(w.ExePath) &&
                   string.Equals(ExePath, w.ExePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public class DockWindow : BarWindow
    {
        readonly StackPanel panel = new StackPanel { Orientation = Orientation.Horizontal };
        readonly List<DockItem> pinned = new List<DockItem>();
        readonly List<DockItem> running = new List<DockItem>();
        readonly Border separator = new Border { Width = 1, Margin = new Thickness(6, 6, 6, 10) };
        readonly DispatcherTimer debounce;
        readonly List<IntPtr> hooks = new List<IntPtr>();
        WinEventProc hookProc;
        IntPtr lastForeground;
        const int GapDip = 4;

        // drag-to-reorder state
        DockItem pressed; Point pressPoint; bool dragging;

        public DockWindow(Settings s) : base(s)
        {
            SizeToContent = SizeToContent.WidthAndHeight;
            Bar.Child = panel;
            Bar.BorderThickness = new Thickness(1);
            Bar.Padding = new Thickness(8, 6, 8, 2);
            panel.Background = Brushes.Transparent; // no gaps between icons, so moving across doesn't reset the zoom
            panel.MouseLeave += delegate { Magnify(null); };

            debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            debounce.Tick += delegate { debounce.Stop(); Refresh(); };

            SourceInitialized += delegate
            {
                ApplyAppearance();
                hookProc = OnWinEvent;
                // show/hide/destroy, name change, cloak/uncloak, foreground, minimize start/end
                uint[][] ranges = { new uint[] { 0x8001, 0x8003 }, new uint[] { 0x800C, 0x800C }, new uint[] { 0x8017, 0x8018 }, new uint[] { 3, 3 }, new uint[] { 0x16, 0x17 } };
                foreach (var r in ranges) hooks.Add(Native.SetWinEventHook(r[0], r[1], IntPtr.Zero, hookProc, 0, 0, 2));
            };
            Closed += delegate { foreach (var h in hooks) Native.UnhookWinEvent(h); };
            SizeChanged += delegate { Reposition(); };
            Loaded += delegate { RebuildPinned(); Refresh(); };

            var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            poll.Tick += delegate { Refresh(); };
            poll.Start();

            var autoHide = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            autoHide.Tick += delegate { AutoHideTick(); };
            autoHide.Start();
        }

        void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || idChild != 0) return;
            if (evt == 3 && hwnd != IntPtr.Zero && Native.GetPid(hwnd) != (uint)System.Diagnostics.Process.GetCurrentProcess().Id)
                lastForeground = hwnd;
            debounce.Stop(); debounce.Start();
        }

        public override void ApplyAppearance()
        {
            base.ApplyAppearance();
            // Current Windows 11 builds paint the system blur over the whole window rectangle and ignore
            // the rounded window region, which drew a square box around the rounded dock. So the dock
            // draws only its own rounded tint, a little denser when blur is on to keep the same contrast.
            Theme.ApplyBlur(Hwnd, 0);
            Bar.Background = Theme.BarTint(S.Blur > 0 ? Math.Min(100, S.Opacity + 15) : S.Opacity);
            Bar.CornerRadius = new CornerRadius(Radius());
            Bar.BorderBrush = Theme.Dark ? new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00));
            separator.Background = Theme.FgAlpha(0x40);
            // No halo effect on the dock: it has its own tint behind the icons, and an effect over the whole
            // panel had to be redrawn on every frame of the hover magnification.
            panel.Effect = null;
            // Transparent space above the dock for magnified and bouncing icons (clicks pass through it).
            Bar.Margin = new Thickness(0, Headroom(), 0, 0);
            Reposition();
        }

        double Radius() { return Math.Round((S.IconSize + 14) * 0.32); }

        /// <summary>Room above the dock (DIP) for an icon at full magnification or mid-bounce.</summary>
        double Headroom() { return Math.Ceiling(Math.Max(14, S.IconSize * (S.DockMagnify / 100.0 - 1))) + 2; }

        /// <summary>Icon size in real screen pixels, so icons are drawn 1:1 without extra scaling blur.</summary>
        int IconPx() { return (int)Math.Round(S.IconSize * Scale); }

        /// <summary>Pixels reserved at the bottom of the primary screen (0 unless "reserve space" is on).</summary>
        public int ReservedPx;

        public override void Reposition()
        {
            if (Hwnd == IntPtr.Zero || ActualWidth <= 0) return;
            double sc = Scale;
            int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
            int w = (int)Math.Round(ActualWidth * sc), h = (int)Math.Round(ActualHeight * sc);
            int head = (int)Math.Round(Headroom() * sc), barH = Math.Max(1, h - head);
            int gap = (int)Math.Round(GapDip * sc);
            // Always registered so we hear about full-screen apps; thickness 0 when not reserving space.
            if (!AppBar.Registered) AppBar.Register(Hwnd);
            bool reserve = S.DockReservesSpace && S.DockAutoHide == 0;
            int thick = reserve ? barH + gap * 2 : 0;
            ReservedPx = thick;
            RECT rc = AppBar.SetPos(AppBar.ABE_BOTTOM, new RECT { Left = 0, Top = sh - thick, Right = sw, Bottom = sh }, thick);
            int bottom = reserve ? rc.Bottom : sh;
            baseX = (sw - w) / 2; baseY = bottom - gap - h; pxW = w; pxH = h; pxGap = gap; pxHead = head;
            Place();
            // No clipping region: the window is taller than the dock so magnified icons can grow above it.
            Native.SetWindowRgn(Hwnd, IntPtr.Zero, true);
        }

        // ---------- auto-hide ----------
        // progress: 0 = fully shown, 1 = fully hidden below the screen edge.
        int baseX, baseY, pxW, pxH, pxGap, pxHead;
        double progress, target;
        DateTime lastWanted = DateTime.Now;
        bool menuOpen, overlapCache; DateTime overlapCheckedAt;

        /// <summary>Keeps the dock up (e.g. while the app page is open from the Windows key).</summary>
        public bool HoldShown;

        void Place()
        {
            double e = progress * progress * (3 - 2 * progress); // smoothstep
            int y = baseY + (int)Math.Round(e * (pxH + pxGap + 4));
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, baseX, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        void AutoHideTick()
        {
            if (Hwnd == IntPtr.Zero || pxW == 0) return;
            int mode = S.DockAutoHide;
            bool hide = false;
            if (mode != 0)
            {
                POINT p; Native.GetCursorPos(out p);
                int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
                bool atEdge = p.Y >= sh - 2 && p.Y < sh + 2 && p.X >= 0 && p.X < sw;
                bool overDock = progress < 0.6 && p.X >= baseX - 12 && p.X <= baseX + pxW + 12 && p.Y >= baseY + pxHead - 16 && p.Y < sh + 2;
                bool busy = menuOpen || dragging || pressed != null || HoldShown;
                bool wantShown = atEdge || overDock || busy;
                if (mode == 2 && !wantShown) wantShown = !OverlapsFocusedWindow();
                if (wantShown) lastWanted = DateTime.Now;
                // short delay before hiding so the dock doesn't flicker when the mouse brushes past
                hide = !wantShown && (DateTime.Now - lastWanted).TotalMilliseconds > (mode == 2 ? 150 : 450);
            }
            target = hide ? 1 : 0;
            if (progress != target && !sliding)
            {
                // Animate on WPF's render clock (once per frame) and by elapsed time, not timer ticks:
                // DispatcherTimer only fires every ~15.6 ms and unevenly, which made the slide stutter.
                sliding = true; slideClock.Restart();
                CompositionTarget.Rendering += SlideFrame;
            }
        }

        bool sliding;
        readonly System.Diagnostics.Stopwatch slideClock = new System.Diagnostics.Stopwatch();
        const double SlideSeconds = 0.2;

        void SlideFrame(object sender, EventArgs e)
        {
            double dt = Math.Min(0.05, slideClock.Elapsed.TotalSeconds); slideClock.Restart();
            double step = dt / SlideSeconds;
            progress = target > progress ? Math.Min(target, progress + step) : Math.Max(target, progress - step);
            Place();
            if (progress == target) { CompositionTarget.Rendering -= SlideFrame; sliding = false; }
        }

        /// <summary>Intellihide: is the focused, non-minimized app window overlapping the dock's area?</summary>
        bool OverlapsFocusedWindow()
        {
            if ((DateTime.Now - overlapCheckedAt).TotalMilliseconds < 250) return overlapCache;
            overlapCheckedAt = DateTime.Now;
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || Native.GetPid(fg) == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return overlapCache;
            string cls = Native.GetClass(fg);
            if (cls == "Progman" || cls == "WorkerW" || Native.IsIconic(fg)) { overlapCache = false; return false; }
            RECT r; Native.GetWindowRect(fg, out r);
            overlapCache = r.Right > baseX && r.Left < baseX + pxW && r.Bottom > baseY + pxHead && r.Top < baseY + pxH;
            return overlapCache;
        }

        // ---------- model ----------

        FrameworkElement appsButton;

        /// <summary>Launchpad-style "Apps" button, always first in the dock; opens the full-screen app page.</summary>
        FrameworkElement BuildAppsButton()
        {
            double size = S.IconSize;
            double space = Math.Round(S.IconSize * 0.14);
            var icon = AppsIcon.Create(size);
            var scale = new ScaleTransform(1, 1);
            icon.RenderTransform = scale; icon.RenderTransformOrigin = new Point(0.5, 1);
            var stack = new StackPanel { Margin = new Thickness(space / 2 + 1, 0, space / 2 + 1, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            stack.Children.Add(icon);
            stack.Children.Add(new Ellipse { Width = 4, Height = 4, Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Hidden }); // keeps alignment with app icons
            stack.ToolTip = "Apps";
            ToolTipService.SetPlacement(stack, System.Windows.Controls.Primitives.PlacementMode.Top);
            ToolTipService.SetInitialShowDelay(stack, 250);
            ToolTipService.SetVerticalOffset(stack, -4);
            stack.MouseEnter += delegate { Magnify(stack); };
            stack.MouseLeftButtonUp += delegate { App.Instance.ToggleAppsPage(); };
            return stack;
        }

        public void RebuildPinned()
        {
            appsButton = BuildAppsButton();
            foreach (var p in pinned) p.Windows.Clear();
            pinned.Clear();
            foreach (var pin in S.Pinned)
            {
                var item = new DockItem { Pin = pin, Aumid = pin.Aumid, ExePath = pin.ExePath, Name = pin.Name, LaunchTarget = pin.Target };
                item.IsUwp = !string.IsNullOrEmpty(pin.Aumid) && string.IsNullOrEmpty(pin.ExePath);
                BuildView(item, IconLoader.LoadSharp(pin.Target, IconPx()));
                pinned.Add(item);
            }
            foreach (var r in running) BuildView(r, r.Img.Source);
            Refresh();
        }

        public void Refresh()
        {
            if (dragging) return;
            var wins = WindowTracker.GetAppWindows();
            if (Demo.On) wins.Clear(); // no real running apps in screenshots
            foreach (var i in pinned) i.Windows.Clear();
            foreach (var i in running) i.Windows.Clear();
            foreach (var w in wins)
            {
                var item = pinned.FirstOrDefault(p => p.Matches(w)) ?? running.FirstOrDefault(r => r.Matches(w));
                if (item == null)
                {
                    item = new DockItem { Aumid = w.Aumid, ExePath = w.ExePath, IsUwp = w.IsUwp };
                    bool useAumid = w.IsUwp && !string.IsNullOrEmpty(w.Aumid);
                    item.LaunchTarget = useAumid ? "shell:AppsFolder\\" + w.Aumid : w.ExePath;
                    item.Name = w.IsUwp ? w.Title : (AppNames.ForExe(w.ExePath) ?? w.Title);
                    string iconSource = useAumid ? "shell:AppsFolder\\" + w.Aumid : w.ExePath;
                    string lnk = useAumid ? null : ShellUtil.ShortcutForIconless(w.ExePath);
                    if (lnk != null)
                    {
                        // Helper binary without an icon (e.g. NoMachine's nxplayer.bin): borrow the app's shortcut.
                        iconSource = lnk;
                        item.LaunchTarget = lnk;
                        item.Name = System.IO.Path.GetFileNameWithoutExtension(lnk);
                    }
                    ImageSource icon = IconLoader.LoadSharp(iconSource, IconPx());
                    if (icon == null) icon = IconLoader.ForWindow(w.Hwnd);
                    BuildView(item, icon);
                    running.Add(item);
                }
                item.Windows.Add(w);
            }
            running.RemoveAll(r => r.Windows.Count == 0);

            // Sync the visual tree only when the sequence actually changed.
            var desired = new List<UIElement>();
            if (appsButton != null) desired.Add(appsButton);
            foreach (var p in pinned) desired.Add(p.View);
            if (running.Count > 0 && pinned.Count > 0) desired.Add(separator);
            foreach (var r in running) desired.Add(r.View);
            bool same = desired.Count == panel.Children.Count;
            for (int i = 0; same && i < desired.Count; i++) same = panel.Children[i] == desired[i];
            if (!same)
            {
                panel.Children.Clear();
                foreach (var e in desired) panel.Children.Add(e);
            }
            foreach (var i in pinned.Concat(running))
            {
                i.Dot.Visibility = i.Windows.Count > 0 ? Visibility.Visible : Visibility.Hidden;
                if (i.Pin == null && i.Windows.Count > 0 && i.IsUwp) i.Name = i.Windows[0].Title;
                i.View.ToolTip = i.Name;
            }
        }

        // ---------- views ----------

        void BuildView(DockItem item, ImageSource icon)
        {
            double size = S.IconSize;
            item.Img = new Image { Source = icon, Width = size, Height = size };
            RenderOptions.SetBitmapScalingMode(item.Img, BitmapScalingMode.HighQuality);
            item.Dot = new Ellipse { Width = 4, Height = 4, Fill = Theme.Fg, Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Hidden };
            double space = Math.Round(S.IconSize * 0.14); // ~7px between 48px icons
            var stack = new StackPanel { Margin = new Thickness(space / 2 + 1, 0, space / 2 + 1, 0), Background = Brushes.Transparent };
            stack.Children.Add(item.Img);
            stack.Children.Add(item.Dot);
            item.Scale = new ScaleTransform(1, 1);
            item.Bounce = new TranslateTransform();
            var tg = new TransformGroup();
            tg.Children.Add(item.Scale); tg.Children.Add(item.Bounce);
            item.Img.RenderTransform = tg;
            item.Img.RenderTransformOrigin = new Point(0.5, 1);
            ToolTipService.SetPlacement(stack, System.Windows.Controls.Primitives.PlacementMode.Top);
            ToolTipService.SetInitialShowDelay(stack, 250);
            ToolTipService.SetVerticalOffset(stack, -4);
            stack.ToolTip = item.Name;

            stack.MouseEnter += delegate { Magnify(stack); };
            stack.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                pressed = item; pressPoint = e.GetPosition(panel); dragging = false;
                stack.CaptureMouse();
            };
            stack.MouseMove += delegate (object s, MouseEventArgs e)
            {
                if (pressed != item || e.LeftButton != MouseButtonState.Pressed || item.Pin == null) return;
                Point p = e.GetPosition(panel);
                if (!dragging && Math.Abs(p.X - pressPoint.X) > 8) dragging = true;
                if (dragging) DragTo(item, p.X);
            };
            stack.MouseLeftButtonUp += delegate
            {
                stack.ReleaseMouseCapture();
                bool wasDrag = dragging;
                dragging = false;
                if (pressed != item) return;
                pressed = null;
                if (wasDrag) { S.Pinned = pinned.Select(x => x.Pin).ToList(); S.Save(); Refresh(); }
                else if (stack.IsMouseOver) OnClick(item);
            };
            stack.MouseRightButtonUp += delegate { ShowMenu(item, stack); };
            item.View = stack;
        }

        /// <summary>
        /// macOS-style magnification: the icon under the mouse grows the most and its neighbors a little,
        /// all anchored at the bottom. over == null puts every icon back to normal size.
        /// </summary>
        void Magnify(UIElement over)
        {
            double mag = S.DockMagnify / 100.0;
            var icons = new List<KeyValuePair<UIElement, ScaleTransform>>();
            foreach (UIElement c in panel.Children)
            {
                var st = ScaleOf(c);
                if (st != null) icons.Add(new KeyValuePair<UIElement, ScaleTransform>(c, st));
            }
            int idx = over == null ? -1 : icons.FindIndex(k => k.Key == over);
            for (int i = 0; i < icons.Count; i++)
            {
                double sc = 1;
                if (idx >= 0 && mag > 1.001)
                {
                    int d = Math.Abs(i - idx);
                    sc = d == 0 ? mag : d == 1 ? 1 + (mag - 1) * 0.45 : d == 2 ? 1 + (mag - 1) * 0.15 : 1;
                }
                Animate(icons[i].Value, sc);
            }
        }

        static ScaleTransform ScaleOf(UIElement e)
        {
            var p = e as Panel;
            if (p == null || p.Children.Count == 0) return null;
            var t = p.Children[0].RenderTransform;
            var st = t as ScaleTransform;
            if (st != null) return st;
            var g = t as TransformGroup;
            if (g != null) foreach (var c in g.Children) if (c is ScaleTransform) return (ScaleTransform)c;
            return null;
        }

        static void Animate(ScaleTransform t, double to)
        {
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(120)) { EasingFunction = new QuadraticEase() };
            t.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            t.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        void DragTo(DockItem item, double x)
        {
            int from = pinned.IndexOf(item);
            int to = 0;
            for (int i = 0; i < pinned.Count; i++)
            {
                var v = pinned[i].View;
                double mid = v.TranslatePoint(new Point(v.ActualWidth / 2, 0), panel).X;
                if (x > mid) to = i;
            }
            if (x < pinned[0].View.TranslatePoint(new Point(pinned[0].View.ActualWidth / 2, 0), panel).X) to = 0;
            if (to == from || from < 0) return;
            pinned.RemoveAt(from);
            pinned.Insert(to, item);
            panel.Children.Remove(item.View);
            panel.Children.Insert(to, item.View);
        }

        void BounceOnce(DockItem item)
        {
            var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(900) };
            a.KeyFrames.Add(new EasingDoubleKeyFrame(-14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(220)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(450)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(-7, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            item.Bounce.BeginAnimation(TranslateTransform.YProperty, a);
        }

        // ---------- actions ----------

        void OnClick(DockItem item)
        {
            if (item.Windows.Count == 0) { Launch(item); return; }
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) fg = lastForeground;
            int idx = item.Windows.FindIndex(w => w.Hwnd == fg);
            if (idx >= 0 && !Native.IsIconic(fg))
            {
                if (item.Windows.Count == 1) Native.ShowWindow(fg, Native.SW_MINIMIZE);
                else WindowTracker.Activate(item.Windows[item.Windows.Count - 1].Hwnd); // cycle to the back-most window
            }
            else WindowTracker.Activate(item.Windows[0].Hwnd); // front-most window of the app
        }

        void Launch(DockItem item)
        {
            if (string.IsNullOrEmpty(item.LaunchTarget)) return;
            BounceOnce(item);
            ShellUtil.Open(item.LaunchTarget);
            debounce.Stop(); debounce.Start();
        }

        void ShowMenu(DockItem item, FrameworkElement anchor)
        {
            var m = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
            m.Opened += delegate { menuOpen = true; };
            m.Closed += delegate { menuOpen = false; };
            foreach (var w in item.Windows)
            {
                var win = w;
                string t = string.IsNullOrEmpty(w.Title) ? item.Name : w.Title;
                if (t.Length > 60) t = t.Substring(0, 57) + "…";
                m.Items.Add(Mi(t, delegate { WindowTracker.Activate(win.Hwnd); }));
            }
            if (item.Windows.Count > 0) m.Items.Add(new Separator());
            if (item.Pin != null)
                m.Items.Add(Mi("Remove from Dock", delegate { S.Pinned.Remove(item.Pin); S.Save(); RebuildPinned(); }));
            else
                m.Items.Add(Mi("Keep in Dock", delegate
                {
                    S.Pinned.Add(new PinnedApp { Target = item.LaunchTarget, Aumid = item.Aumid, ExePath = item.IsUwp ? null : item.ExePath, Name = item.Name });
                    S.Save();
                    running.Remove(item);
                    RebuildPinned();
                }));
            m.Items.Add(Mi(item.Windows.Count > 0 ? "New Window" : "Open", delegate { Launch(item); }));
            if (!item.IsUwp && !string.IsNullOrEmpty(item.ExePath))
                m.Items.Add(Mi("Show in File Explorer", delegate { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + item.ExePath + "\""); }));
            if (item.Windows.Count > 0)
            {
                m.Items.Add(new Separator());
                m.Items.Add(Mi(item.Windows.Count > 1 ? "Quit (close all windows)" : "Quit", delegate
                {
                    foreach (var w in item.Windows) Native.PostMessage(w.Hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }));
            }
            m.IsOpen = true;
        }

        static MenuItem Mi(string header, Action a)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += delegate { a(); };
            return mi;
        }
    }
}
