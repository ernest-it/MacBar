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
            Bar.CornerRadius = new CornerRadius(Radius());
            Bar.BorderBrush = Theme.Dark ? new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)) : new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00));
            separator.Background = Theme.FgAlpha(0x40);
            panel.Effect = Theme.Halo();
            Reposition();
        }

        double Radius() { return Math.Round((S.IconSize + 14) * 0.32); }

        public override void Reposition()
        {
            if (Hwnd == IntPtr.Zero || ActualWidth <= 0) return;
            double sc = Scale;
            int sw = Native.GetSystemMetrics(0), sh = Native.GetSystemMetrics(1);
            int w = (int)Math.Round(ActualWidth * sc), h = (int)Math.Round(ActualHeight * sc);
            int gap = (int)Math.Round(GapDip * sc);
            // Always registered so we hear about full-screen apps; thickness 0 when not reserving space.
            if (!AppBar.Registered) AppBar.Register(Hwnd);
            int thick = S.DockReservesSpace ? h + gap * 2 : 0;
            RECT rc = AppBar.SetPos(AppBar.ABE_BOTTOM, new RECT { Left = 0, Top = sh - thick, Right = sw, Bottom = sh }, thick);
            int bottom = S.DockReservesSpace ? rc.Bottom : sh;
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, (sw - w) / 2, bottom - gap - h, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Theme.SetRegion(Hwnd, w, h, (int)Math.Round(Radius() * sc));
        }

        // ---------- model ----------

        public void RebuildPinned()
        {
            foreach (var p in pinned) p.Windows.Clear();
            pinned.Clear();
            foreach (var pin in S.Pinned)
            {
                var item = new DockItem { Pin = pin, Aumid = pin.Aumid, ExePath = pin.ExePath, Name = pin.Name, LaunchTarget = pin.Target };
                item.IsUwp = !string.IsNullOrEmpty(pin.Aumid) && string.IsNullOrEmpty(pin.ExePath);
                BuildView(item, IconLoader.Load(pin.Target, 96));
                pinned.Add(item);
            }
            foreach (var r in running) BuildView(r, r.Img.Source);
            Refresh();
        }

        public void Refresh()
        {
            if (dragging) return;
            var wins = WindowTracker.GetAppWindows();
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
                    ImageSource icon = useAumid ? IconLoader.Load("shell:AppsFolder\\" + w.Aumid, 96) : IconLoader.Load(w.ExePath, 96);
                    if (icon == null) icon = IconLoader.ForWindow(w.Hwnd);
                    BuildView(item, icon);
                    running.Add(item);
                }
                item.Windows.Add(w);
            }
            running.RemoveAll(r => r.Windows.Count == 0);

            // Sync the visual tree only when the sequence actually changed.
            var desired = new List<UIElement>();
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

            stack.MouseEnter += delegate { Animate(item.Scale, 1.1); };
            stack.MouseLeave += delegate { Animate(item.Scale, 1.0); };
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
