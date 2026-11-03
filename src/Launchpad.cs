using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace MacBar
{
    public class AppEntry
    {
        public string Name, Path;
        public volatile ImageSource Icon;
        public string Target { get { return "shell:AppsFolder\\" + Path; } }
    }

    /// <summary>
    /// Every launchable app, read from the shell's "AppsFolder" (the same list as Start → All apps,
    /// covering Store and desktop apps). Enumerated and iconized on a background STA thread.
    /// </summary>
    public static class AppCatalog
    {
        static List<AppEntry> apps = new List<AppEntry>();
        static DateTime loadedAt = DateTime.MinValue;
        static bool loading;
        public static event Action ListChanged, IconsChanged;

        public static List<AppEntry> Apps { get { lock (typeof(AppCatalog)) return apps; } }
        public static bool Stale { get { return (DateTime.Now - loadedAt).TotalMinutes > 5; } }

        public static void Refresh(Dispatcher ui, int iconPx)
        {
            lock (typeof(AppCatalog)) { if (loading) return; loading = true; }
            var t = new Thread(delegate ()
            {
                try
                {
                    var list = Enumerate();
                    var old = Apps.ToDictionary(a => a.Path, a => a.Icon, StringComparer.OrdinalIgnoreCase);
                    foreach (var a in list) { ImageSource ic; if (old.TryGetValue(a.Path, out ic)) a.Icon = ic; }
                    lock (typeof(AppCatalog)) { apps = list; loadedAt = DateTime.Now; }
                    ui.BeginInvoke(new Action(delegate { if (ListChanged != null) ListChanged(); }));
                    int n = 0;
                    foreach (var a in list)
                    {
                        if (a.Icon != null) continue;
                        a.Icon = IconLoader.LoadSharp(a.Target, iconPx);
                        if (++n % 12 == 0) ui.BeginInvoke(new Action(delegate { if (IconsChanged != null) IconsChanged(); }));
                    }
                    ui.BeginInvoke(new Action(delegate { if (IconsChanged != null) IconsChanged(); }));
                }
                catch { }
                finally { lock (typeof(AppCatalog)) loading = false; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        static object Call(object o, string name, BindingFlags kind, params object[] args)
        {
            return o.GetType().InvokeMember(name, kind, null, o, args);
        }

        static List<AppEntry> Enumerate()
        {
            var result = new List<AppEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
            try
            {
                object folder = Call(shell, "NameSpace", BindingFlags.InvokeMethod, "shell:AppsFolder");
                object items = Call(folder, "Items", BindingFlags.InvokeMethod);
                int count = (int)Call(items, "Count", BindingFlags.GetProperty);
                for (int i = 0; i < count; i++)
                {
                    object item = Call(items, "Item", BindingFlags.InvokeMethod, i);
                    string name = (Call(item, "Name", BindingFlags.GetProperty) as string ?? "").Trim();
                    string path = Call(item, "Path", BindingFlags.GetProperty) as string ?? "";
                    Marshal.ReleaseComObject(item);
                    if (name.Length == 0 || path.Length == 0 || Junk(name, path) || !seen.Add(name)) continue;
                    result.Add(new AppEntry { Name = name, Path = path });
                }
                Marshal.ReleaseComObject(items); Marshal.ReleaseComObject(folder);
            }
            finally { Marshal.ReleaseComObject(shell); }
            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }

        /// <summary>Skips uninstallers, readmes, help files and web links that installers drop into Start.</summary>
        static bool Junk(string name, string path)
        {
            string n = name.ToLowerInvariant(), p = path.ToLowerInvariant();
            string[] words = { "uninstall", "readme", "read me", "release notes", "documentation", "license", "website", "user guide", "manual", "changelog" };
            if (words.Any(w => n.Contains(w))) return true;
            if (n == "help" || n.EndsWith(" help")) return true;
            if (p.StartsWith("http:") || p.StartsWith("https:")) return true;
            string[] exts = { ".url", ".txt", ".pdf", ".chm", ".htm", ".html", ".rtf", ".md", ".hlp" };
            return exts.Any(e => p.EndsWith(e));
        }

        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, int flags, IntPtr token, out IntPtr path);

        /// <summary>AppsFolder paths for desktop apps look like "{KnownFolderGUID}\Vendor\app.exe"; expand to a real path.</summary>
        public static string ExpandPath(string path)
        {
            if (path.StartsWith("{") && path.IndexOf('}') > 0)
            {
                try
                {
                    var g = new Guid(path.Substring(0, path.IndexOf('}') + 1));
                    IntPtr p;
                    if (SHGetKnownFolderPath(ref g, 0, IntPtr.Zero, out p) == 0)
                    {
                        string root = Marshal.PtrToStringUni(p);
                        Marshal.FreeCoTaskMem(p);
                        return System.IO.Path.Combine(root, path.Substring(path.IndexOf('}') + 1).TrimStart('\\'));
                    }
                }
                catch { }
            }
            return path;
        }

        public static PinnedApp ToPin(AppEntry a)
        {
            string expanded = ExpandPath(a.Path);
            bool isFile = expanded.Length > 2 && expanded[1] == ':';
            return new PinnedApp
            {
                Target = a.Target, Name = a.Name,
                ExePath = isFile && expanded.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? expanded : null,
                Aumid = isFile ? null : a.Path
            };
        }
    }

    /// <summary>Launchpad-style full-screen app page: blurred backdrop, search, paged icon grid.</summary>
    public class LaunchpadWindow : Window
    {
        const double IconDip = 72, CellW = 150, CellH = 128;
        readonly Grid root = new Grid();
        readonly Grid content = new Grid();
        readonly TextBox search;
        readonly TextBlock placeholder;
        readonly Grid pagesHost = new Grid { ClipToBounds = true, Background = Brushes.Transparent };
        readonly StackPanel pages = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        readonly TranslateTransform slide = new TranslateTransform();
        readonly ScaleTransform zoom = new ScaleTransform(1, 1);
        // Bottom margin keeps the page dots above the dock.
        readonly StackPanel dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 110) };
        readonly Dictionary<AppEntry, Image> images = new Dictionary<AppEntry, Image>();
        readonly StackPanel results = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        readonly StackPanel fileSection = new StackPanel();
        Action topResult; // what Enter opens
        static ImageSource settingsIcon;
        List<AppEntry> shown = new List<AppEntry>();
        int cols = 7, rows = 5, page, pageCount;
        double cellW = CellW, cellH = CellH;
        DateTime lastWheel;
        IntPtr hwnd;
        bool closing;

        public LaunchpadWindow()
        {
            // Not a per-pixel-alpha (layered) window: at full screen those force WPF to copy every frame
            // back to system memory, which made paging and opening stutter. Transparency comes from DWM
            // instead (frame extended over the client area), and fades use whole-window alpha.
            WindowStyle = WindowStyle.None; AllowsTransparency = false; Background = Brushes.Transparent;
            Topmost = true; ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
            FontFamily = Theme.Font; UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            root.Background = new SolidColorBrush(Color.FromArgb(0x66, 0x08, 0x08, 0x0C)); // hit-testable, so clicks on empty space close
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition());
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RenderTransform = zoom;
            content.RenderTransformOrigin = new Point(0.5, 0.5);

            // Search pill
            var pill = new Border
            {
                Width = 340, Height = 34, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 64, 0, 28), HorizontalAlignment = HorizontalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1)
            };
            var sp = new DockPanel { Margin = new Thickness(10, 0, 10, 0) };
            var glass = new TextBlock { Text = "", FontFamily = Glyphs.IconFont, FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(glass, Dock.Left);
            sp.Children.Add(glass);
            var field = new Grid();
            placeholder = new TextBlock { Text = "Search apps, settings and files", FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            search = new TextBox { FontSize = 14, Foreground = Brushes.White, CaretBrush = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, SelectionBrush = new SolidColorBrush(Theme.Accent) };
            field.Children.Add(placeholder); field.Children.Add(search);
            sp.Children.Add(field);
            pill.Child = sp;
            content.Children.Add(pill);

            pages.RenderTransform = slide;
            pagesHost.Children.Add(pages);
            Grid.SetRow(pagesHost, 1);
            content.Children.Add(pagesHost);
            // Search results (apps, Settings pages, files) replace the pages while typing.
            results.Visibility = Visibility.Collapsed;
            Grid.SetRow(results, 1);
            content.Children.Add(results);
            Grid.SetRow(dots, 2);
            content.Children.Add(dots);
            root.Children.Add(content);
            Content = root;

            search.TextChanged += delegate { placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Filter(); };
            root.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                // Clicking anywhere that isn't an app, the search field or the page dots closes the page (like macOS).
                // (App tiles mark the click handled, so they never reach here.)
                var d = e.OriginalSource as DependencyObject;
                if (!Within(d, pill) && !Within(d, dots)) Dismiss();
            };
            PreviewMouseWheel += delegate (object s, MouseWheelEventArgs e)
            {
                if ((DateTime.Now - lastWheel).TotalMilliseconds < 320) return;
                lastWheel = DateTime.Now;
                SetPage(page + (e.Delta < 0 ? 1 : -1), true);
            };
            PreviewKeyDown += OnKey;
            Deactivated += delegate { if (IsVisible && !closing) Dismiss(); };
            SourceInitialized += delegate
            {
                hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW);
                HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                var m = new MARGINS { L = -1, R = -1, T = -1, B = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref m);
                Native.SetAccent(hwnd, 4, 0x40100C0C); // acrylic blur of whatever is behind
            };
            AppCatalog.ListChanged += delegate { if (IsVisible) Filter(); };
            AppCatalog.IconsChanged += UpdateIcons;
        }

        [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int L, R, T, B; }
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
        double alpha;
        EventHandler fadeTick;

        // Window.Opacity only works on layered windows, so fade the content (tint, search, icons) instead.
        void SetAlpha(double a)
        {
            alpha = Math.Max(0, Math.Min(1, a));
            root.Opacity = alpha;
        }

        /// <summary>Fades the content in or out, stepped once per rendered frame.</summary>
        void FadeTo(double to, double ms, Action done)
        {
            if (fadeTick != null) CompositionTarget.Rendering -= fadeTick;
            double from = alpha;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            fadeTick = delegate
            {
                double t = Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
                SetAlpha(from + (to - from) * (1 - Math.Pow(1 - t, 3)));
                if (t < 1) return;
                CompositionTarget.Rendering -= fadeTick; fadeTick = null;
                if (done != null) done();
            };
            CompositionTarget.Rendering += fadeTick;
        }

        static bool Within(DependencyObject d, DependencyObject container)
        {
            while (d != null)
            {
                if (d == container) return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        public void Toggle() { if (IsVisible && !closing) Dismiss(); else Open(); }

        public void Open()
        {
            closing = false;
            var src = PresentationSource.FromVisual(App.Instance.MainBar);
            double scale = src != null ? src.CompositionTarget.TransformToDevice.M11 : 1;
            Left = 0; Top = 0;
            Width = Native.GetSystemMetrics(0) / scale; Height = Native.GetSystemMetrics(1) / scale;
            // 7 columns like macOS (9 on ultrawide screens); cells grow to fill the screen.
            double aspect = Width / Height;
            cols = aspect > 2 ? 9 : Width >= 1500 ? 7 : Width >= 1100 ? 6 : 5;
            rows = Math.Max(3, Math.Min(5, (int)((Height - 300) / CellH)));
            cellW = Math.Max(CellW, Math.Min(220, (Width - 240) / cols));
            cellH = Math.Max(CellH, Math.Min(190, (Height - 300) / rows));
            if (AppCatalog.Apps.Count == 0 || AppCatalog.Stale) AppCatalog.Refresh(Dispatcher, (int)Math.Round(IconDip * scale));
            search.Text = "";
            Filter();
            SetAlpha(0);
            Show();
            Activate();
            search.Focus();
            FadeTo(1, 160, null);
            var z = new DoubleAnimation(1.06, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, z);
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, z);
        }

        public void Dismiss()
        {
            if (closing || !IsVisible) return;
            closing = true;
            FadeTo(0, 130, delegate { Hide(); closing = false; });
            var z = new DoubleAnimation(1.04, TimeSpan.FromMilliseconds(130));
            zoom.BeginAnimation(ScaleTransform.ScaleXProperty, z);
            zoom.BeginAnimation(ScaleTransform.ScaleYProperty, z);
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (search.Text.Length > 0) search.Text = ""; else Dismiss();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && topResult != null) { topResult(); e.Handled = true; }
            else if ((e.Key == Key.Right || e.Key == Key.PageDown) && search.Text.Length == 0) { SetPage(page + 1, true); e.Handled = true; }
            else if ((e.Key == Key.Left || e.Key == Key.PageUp) && search.Text.Length == 0) { SetPage(page - 1, true); e.Handled = true; }
        }

        void Filter()
        {
            var all = AppCatalog.Apps;
            string q = search.Text.Trim();
            if (q.Length == 0)
            {
                shown = all;
                results.Visibility = Visibility.Collapsed;
                pagesHost.Visibility = Visibility.Visible;
                topResult = shown.Count > 0 ? (Action)delegate { Launch(shown[0]); } : null;
                FileSearch.Query("", 0, Dispatcher, delegate { });
                BuildPages();
            }
            else
            {
                // Best matches first: name starts with the query, then a word starts with it, then contains it.
                Func<AppEntry, int> rank = a =>
                {
                    string n = a.Name;
                    if (n.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)) return 0;
                    if (n.Split(' ', '-', '.').Any(w => w.StartsWith(q, StringComparison.CurrentCultureIgnoreCase))) return 1;
                    return n.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 ? 2 : 3;
                };
                shown = all.Select(a => new { a, r = rank(a) }).Where(x => x.r < 3).OrderBy(x => x.r).Select(x => x.a).ToList();
                BuildResults(q);
            }
        }

        // ---------- search results: Applications / Settings / Files & Folders ----------

        void BuildResults(string q)
        {
            images.Clear();
            results.Children.Clear();
            pagesHost.Visibility = Visibility.Collapsed;
            dots.Visibility = Visibility.Hidden;
            results.Visibility = Visibility.Visible;
            topResult = null;

            var apps = shown.Take(cols * 2).ToList();
            if (apps.Count > 0)
            {
                topResult = delegate { Launch(apps[0]); };
                results.Children.Add(Section("Applications", apps.Select(a => Tile(a)).ToList()));
            }

            var setPages = SettingsCatalog.Search(q, cols);
            if (setPages.Count > 0)
            {
                if (settingsIcon == null)
                    settingsIcon = IconLoader.LoadSharp("shell:AppsFolder\\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel", IconPx());
                if (topResult == null) topResult = delegate { OpenTarget(setPages[0].Uri); };
                results.Children.Add(Section("Settings", setPages.Select(p =>
                    MakeTile(settingsIcon, p.Name, delegate { OpenTarget(p.Uri); }, null)).ToList()));
            }

            fileSection.Children.Clear();
            results.Children.Add(fileSection);
            var none = new TextBlock { Text = "No Results", FontSize = 18, Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 120, 0, 0) };
            if (apps.Count == 0 && setPages.Count == 0) results.Children.Insert(0, none);

            FileSearch.Query(q, cols, Dispatcher, delegate (string forQuery, List<FileHit> hits)
            {
                if (forQuery != search.Text.Trim() || hits.Count == 0) return;
                results.Children.Remove(none);
                if (topResult == null) topResult = delegate { OpenTarget(hits[0].Path); };
                fileSection.Children.Add(Section(hits.Count > 0 && hits.All(h => h.IsFolder) ? "Folders" : "Files & Folders", hits.Select(h =>
                    MakeTile(IconLoader.LoadSharp(h.Path, IconPx()), h.Name, delegate { OpenTarget(h.Path); }, m =>
                    {
                        var show = new MenuItem { Header = "Show in File Explorer" };
                        show.Click += delegate { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + h.Path + "\""); Dismiss(); };
                        m.Items.Add(show);
                    })).ToList()));
            });
        }

        FrameworkElement Section(string title, List<FrameworkElement> tiles)
        {
            var sp = new StackPanel { Width = cols * cellW, Margin = new Thickness(0, 0, 0, 14) };
            sp.Children.Add(new TextBlock
            {
                Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(14, 0, 0, 2)
            });
            sp.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(14, 4, 14, 6) });
            var grid = new UniformGrid { Columns = cols, HorizontalAlignment = HorizontalAlignment.Left };
            foreach (var t in tiles) { t.Height = Math.Min(cellH, 120); t.Width = cellW; grid.Children.Add(t); }
            sp.Children.Add(grid);
            return sp;
        }

        int IconPx()
        {
            var src = PresentationSource.FromVisual(App.Instance.MainBar);
            return (int)Math.Round(IconDip * (src != null ? src.CompositionTarget.TransformToDevice.M11 : 1));
        }

        void OpenTarget(string target)
        {
            ShellUtil.Open(target);
            Dismiss();
        }

        void BuildPages()
        {
            pages.Children.Clear(); images.Clear();
            int per = cols * rows;
            double hostW = Width;
            var psrc = PresentationSource.FromVisual(App.Instance.MainBar);
            double dpiScale = psrc != null ? psrc.CompositionTarget.TransformToDevice.M11 : 1;
            pageCount = Math.Max(1, (int)Math.Ceiling(shown.Count / (double)per));
            for (int p = 0; p < pageCount; p++)
            {
                var grid = new UniformGrid { Columns = cols, Rows = rows, Width = cols * cellW, Height = rows * cellH, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
                foreach (var a in shown.Skip(p * per).Take(per)) grid.Children.Add(Tile(a));
                // Each page is cached as one texture, so sliding and zooming move bitmaps instead of re-drawing ~45 tiles a frame.
                var holder = new Grid { Width = hostW, Background = Brushes.Transparent, CacheMode = new BitmapCache(dpiScale) { SnapsToDevicePixels = true } };
                holder.Children.Add(grid);
                pages.Children.Add(holder);
            }
            if (shown.Count == 0)
            {
                var none = new TextBlock { Text = "No Results", FontSize = 18, Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 120, 0, 0) };
                ((Grid)pages.Children[0]).Children.Add(none);
            }
            BuildDots();
            SetPage(0, false);
        }

        FrameworkElement Tile(AppEntry a)
        {
            Image img;
            var tile = MakeTile(a.Icon, a.Name, delegate { Launch(a); }, m =>
            {
                var keep = new MenuItem { Header = "Keep in Dock" }; keep.Click += delegate { App.Instance.PinApp(AppCatalog.ToPin(a)); };
                m.Items.Add(keep);
            }, out img);
            images[a] = img;
            return tile;
        }

        FrameworkElement MakeTile(ImageSource icon, string name, Action open, Action<ContextMenu> extraMenu)
        {
            Image img;
            return MakeTile(icon, name, open, extraMenu, out img);
        }

        FrameworkElement MakeTile(ImageSource icon, string name, Action open, Action<ContextMenu> extraMenu, out Image img)
        {
            img = new Image { Width = IconDip, Height = IconDip, Source = icon, Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.35 } };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            var label = new TextBlock
            {
                Text = name, FontSize = 13, Foreground = Brushes.White, TextAlignment = TextAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
                Width = cellW - 18, Margin = new Thickness(0, 8, 0, 0)
            };
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(img); stack.Children.Add(label);
            var tile = new Border { Child = stack, Padding = new Thickness(6, 10, 6, 8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(16), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = name };
            ToolTipService.SetInitialShowDelay(tile, 900);
            tile.MouseEnter += delegate { tile.Background = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)); };
            tile.MouseLeave += delegate { tile.Background = Brushes.Transparent; tile.Opacity = 1; };
            tile.MouseLeftButtonDown += delegate { tile.Opacity = 0.65; };
            tile.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; tile.Opacity = 1; open(); };
            tile.MouseRightButtonUp += delegate (object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                var m = new ContextMenu();
                var o = new MenuItem { Header = "Open" }; o.Click += delegate { open(); };
                m.Items.Add(o);
                if (extraMenu != null) extraMenu(m);
                m.IsOpen = true;
            };
            return tile;
        }

        void UpdateIcons()
        {
            foreach (var kv in images) if (kv.Value.Source == null && kv.Key.Icon != null) kv.Value.Source = kv.Key.Icon;
        }

        void BuildDots()
        {
            dots.Children.Clear();
            dots.Visibility = pageCount > 1 ? Visibility.Visible : Visibility.Hidden;
            for (int i = 0; i < pageCount; i++)
            {
                int idx = i;
                var d = new Border { Width = 18, Height = 18, Background = Brushes.Transparent, Cursor = Cursors.Hand };
                d.Child = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Brushes.White };
                d.MouseLeftButtonUp += delegate (object s, MouseButtonEventArgs e) { e.Handled = true; SetPage(idx, true); };
                dots.Children.Add(d);
            }
        }

        void SetPage(int p, bool animate)
        {
            page = Math.Max(0, Math.Min(pageCount - 1, p));
            double to = -page * Width;
            if (animate && Environment.GetEnvironmentVariable("MACBAR_DEBUG") == "1") MeasureFrames("page slide", 400);
            if (animate) slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(300)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else { slide.BeginAnimation(TranslateTransform.XProperty, null); slide.X = to; }
            for (int i = 0; i < dots.Children.Count; i++)
                ((System.Windows.Shapes.Ellipse)((Border)dots.Children[i]).Child).Opacity = i == page ? 1 : 0.35;
        }

        /// <summary>Debug aid: logs frame count and the longest gap between frames during an animation.</summary>
        void MeasureFrames(string what, int ms)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double last = 0, worst = 0; int frames = 0;
            EventHandler h = null;
            h = delegate
            {
                double now = clock.Elapsed.TotalMilliseconds;
                if (frames > 0) worst = Math.Max(worst, now - last);
                last = now; frames++;
                if (now < ms) return;
                CompositionTarget.Rendering -= h;
                App.Trace(string.Format("{0}: {1} frames in {2:0} ms, worst gap {3:0.0} ms", what, frames, now, worst));
            };
            CompositionTarget.Rendering += h;
        }

        void Launch(AppEntry a)
        {
            ShellUtil.Open(a.Target);
            Dismiss();
        }
    }

    /// <summary>The dock's "Apps" button icon: a dark tile with a 3x3 grid of colored squares, drawn as vectors.</summary>
    public static class AppsIcon
    {
        public static FrameworkElement Create(double size)
        {
            var tile = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.225),
                Background = new LinearGradientBrush(Color.FromRgb(0x56, 0x56, 0x5C), Color.FromRgb(0x1E, 0x1E, 0x22), 90),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0.8)
            };
            string[] colors = { "#FF5F57", "#FEBC2E", "#28C840", "#0A84FF", "#BF5AF2", "#FF9F0A", "#64D2FF", "#FF375F", "#30D158" };
            var grid = new UniformGrid { Rows = 3, Columns = 3, Margin = new Thickness(size * 0.19) };
            foreach (var c in colors)
                grid.Children.Add(new Border { CornerRadius = new CornerRadius(size * 0.045), Margin = new Thickness(size * 0.03), Background = (Brush)new BrushConverter().ConvertFromString(c) });
            tile.Child = grid;
            return tile;
        }
    }
}
