using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace MacBar
{
    /// <summary>Editor for workspaces: capture the open windows, tune each app, preview the layout, run it.</summary>
    public class WorkspacesWindow : Window
    {
        readonly ListBox list = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
        readonly StackPanel detail = new StackPanel { Margin = new Thickness(18, 14, 18, 18) };
        readonly TextBlock status = new TextBlock { Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        readonly Dictionary<WsApp, Border> previewRects = new Dictionary<WsApp, Border>();
        Workspace current;

        static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
        static readonly Brush Accent = new SolidColorBrush(Theme.Accent);

        public WorkspacesWindow()
        {
            Title = "Workspaces"; Width = 900; Height = 720; MinWidth = 760; MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Theme.Font; FontSize = 13; Background = Brushes.White;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());

            var side = new DockPanel { Background = Panel };
            var sideButtons = new StackPanel { Margin = new Thickness(10) };
            DockPanel.SetDock(sideButtons, Dock.Bottom);
            sideButtons.Children.Add(Button("New from Open Windows", NewFromOpen, true));
            if (File.Exists(Workspaces.PowerToysFile)) sideButtons.Children.Add(Button("Import from PowerToys", Import, true));
            sideButtons.Children.Add(Button("Delete", Delete, true));
            side.Children.Add(sideButtons);
            var head = new TextBlock { Text = "Workspaces", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 14, 14, 8) };
            DockPanel.SetDock(head, Dock.Top);
            side.Children.Add(head);
            list.Margin = new Thickness(6, 0, 6, 0);
            list.SelectionChanged += delegate { Show(list.SelectedItem as Workspace); };
            list.DisplayMemberPath = "Name";
            side.Children.Add(list);
            grid.Children.Add(side);

            var scroll = new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(scroll, 1);
            grid.Children.Add(scroll);
            Content = grid;

            Workspaces.Changed += RefreshList;
            Closed += delegate { Workspaces.Changed -= RefreshList; };
            RefreshList();
            if (Workspaces.All.Count > 0) list.SelectedIndex = 0; else Show(null);
        }

        void RefreshList()
        {
            var sel = current;
            list.ItemsSource = null;
            list.ItemsSource = Workspaces.All.ToList();
            if (sel != null && Workspaces.All.Contains(sel)) list.SelectedItem = sel;
        }

        static Button Button(string text, Action click, bool wide)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, wide ? 0 : 8, 6), HorizontalAlignment = wide ? HorizontalAlignment.Stretch : HorizontalAlignment.Left };
            b.Click += delegate { click(); };
            return b;
        }

        // ------------------------------------------------------------ list actions

        void NewFromOpen()
        {
            var ws = new Workspace { Name = "Workspace " + (Workspaces.All.Count + 1) };
            ws.Apps = Workspaces.CaptureOpenWindows();
            Workspaces.All.Add(ws);
            Workspaces.Save();
            list.SelectedItem = ws;
            status.Text = "Saved " + ws.Apps.Count + " windows. Remove the ones you don't want, then rename it.";
        }

        void Import()
        {
            try
            {
                var imported = Workspaces.ImportPowerToys();
                foreach (var ws in imported) { ws.Name += " (PowerToys)"; Workspaces.All.Add(ws); }
                Workspaces.Save();
                if (imported.Count > 0) list.SelectedItem = imported[0];
                MessageBox.Show(this, "Imported " + imported.Count + " workspace(s).\n\nPowerToys doesn't record Chrome profiles: pick each Chrome window's profile in the list. "
                    + "For exact positions, arrange your windows and click \"Update from Open Windows\".", "Workspaces");
            }
            catch (Exception ex) { MessageBox.Show(this, "Couldn't import PowerToys workspaces.\n\n" + ex.Message, "Workspaces"); }
        }

        void Delete()
        {
            if (current == null) return;
            if (MessageBox.Show(this, "Delete \"" + current.Name + "\"?", "Workspaces", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            Workspaces.All.Remove(current);
            current = null;
            Workspaces.Save();
            if (Workspaces.All.Count > 0) list.SelectedIndex = 0; else Show(null);
        }

        // ------------------------------------------------------------ detail

        void Show(Workspace ws)
        {
            current = ws;
            detail.Children.Clear();
            previewRects.Clear();
            if (ws == null)
            {
                detail.Children.Add(new TextBlock { Text = "No workspaces yet", FontSize = 18, FontWeight = FontWeights.SemiBold });
                detail.Children.Add(new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.DimGray,
                    Text = "Open your apps and put each window where you want it, then click \"New from Open Windows\". " +
                           "MacBar remembers each window's spot, including which Chrome profile it belongs to."
                });
                return;
            }

            var name = new TextBox { Text = ws.Name, FontSize = 18, FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 2, 0, 2) };
            name.LostFocus += delegate { if (name.Text.Trim().Length > 0 && name.Text != ws.Name) { ws.Name = name.Text.Trim(); Workspaces.Save(); } };
            name.KeyDown += delegate (object s, KeyEventArgs e) { if (e.Key == Key.Enter) Keyboard.ClearFocus(); };
            detail.Children.Add(name);

            // Run buttons up top.
            var run = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            var openBtn = Button("Open Missing Apps & Arrange", delegate { Run(ws, true); }, false);
            openBtn.FontWeight = FontWeights.SemiBold;
            run.Children.Add(openBtn);
            run.Children.Add(Button("Arrange Open Windows Only", delegate { Run(ws, false); }, false));
            detail.Children.Add(run);
            detail.Children.Add(status);

            detail.Children.Add(Section("Layout"));
            detail.Children.Add(Preview(ws));

            detail.Children.Add(Section("Apps"));
            detail.Children.Add(new TextBlock { Text = "Checked apps are opened when they have no window. Unchecked ones are only moved if they're already open.", Foreground = Brushes.DimGray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            var mons = Workspaces.CurrentMonitors();
            var profiles = Workspaces.ChromeProfiles();
            foreach (var a in ws.Apps.ToList()) detail.Children.Add(AppRow(ws, a, mons, profiles));
            var edit = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            edit.Children.Add(Button("Update from Open Windows", delegate
            {
                if (MessageBox.Show(this, "Replace this workspace's apps and positions with the windows open right now?", "Workspaces", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                Workspaces.UpdateFromOpenWindows(ws); Workspaces.Save(); Show(ws);
                status.Text = "Updated from " + ws.Apps.Count + " open windows.";
            }, false));
            Button add = null;
            add = Button("Add an Open Window…", delegate { AddMenu(ws, add); }, false);
            edit.Children.Add(add);
            detail.Children.Add(edit);

            detail.Children.Add(Section("When I sign in"));
            var signIn = new CheckBox { Content = "Run this workspace automatically when I sign in", IsChecked = ws.RunAtSignIn, Margin = new Thickness(0, 2, 0, 4) };
            var opens = new RadioButton { Content = "Open missing apps, then arrange everything", IsChecked = ws.SignInOpensApps, GroupName = "si" + ws.Id, Margin = new Thickness(22, 2, 0, 2) };
            var only = new RadioButton { Content = "Only arrange windows as they open (don't open anything)", IsChecked = !ws.SignInOpensApps, GroupName = "si" + ws.Id, Margin = new Thickness(22, 2, 0, 2) };
            Action sync = delegate { opens.IsEnabled = only.IsEnabled = ws.RunAtSignIn; };
            signIn.Checked += delegate { foreach (var o in Workspaces.All) o.RunAtSignIn = false; ws.RunAtSignIn = true; Workspaces.Save(); sync(); };
            signIn.Unchecked += delegate { ws.RunAtSignIn = false; Workspaces.Save(); sync(); };
            opens.Checked += delegate { ws.SignInOpensApps = true; Workspaces.Save(); };
            only.Checked += delegate { ws.SignInOpensApps = false; Workspaces.Save(); };
            sync();
            detail.Children.Add(signIn); detail.Children.Add(opens); detail.Children.Add(only);
            detail.Children.Add(new TextBlock { Text = "For 90 seconds after sign-in MacBar keeps placing windows as apps that start with Windows show up. Make sure \"Start MacBar when I sign in\" is on in MacBar Settings.", Foreground = Brushes.DimGray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 4, 0, 0) });
        }

        static TextBlock Section(string text)
        {
            return new TextBlock { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) };
        }

        void Run(Workspace ws, bool open)
        {
            status.Text = open ? "Opening and arranging…" : "Arranging…";
            Workspaces.Run(ws, open, open ? 20 : 4, Dispatcher, delegate (Workspaces.Result r)
            {
                status.Text = r.Arranged + " window(s) arranged" + (r.Opened > 0 ? ", " + r.Opened + " app(s) opened" : "") + "." +
                              (r.Problems.Count > 0 ? "\n" + string.Join("\n", r.Problems) : "");
                Workspaces.ShowResult(r);
            });
        }

        void AddMenu(Workspace ws, FrameworkElement anchor)
        {
            var open = Workspaces.CaptureOpenWindows();
            var m = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var a in open)
            {
                var app = a;
                bool already = ws.Apps.Any(x => string.Equals(x.Key, app.Key, StringComparison.OrdinalIgnoreCase) && x.TitleHint == app.TitleHint);
                var mi = new MenuItem { Header = app.Name + (app.TitleHint != null && app.TitleHint != app.Name ? "  —  " + Trim(app.TitleHint, 50) : ""), IsEnabled = !already };
                mi.Click += delegate { ws.Apps.Add(app); Workspaces.Save(); Show(ws); };
                m.Items.Add(mi);
            }
            if (m.Items.Count == 0) m.Items.Add(new MenuItem { Header = "No open windows", IsEnabled = false });
            m.IsOpen = true;
        }

        static string Trim(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        static string MonitorName(WsApp a, List<Workspaces.MonitorInfo> mons)
        {
            var sorted = mons.OrderBy(m => m.L).ToList();
            var m0 = sorted.FirstOrDefault(m => m.L == a.MonL && m.T == a.MonT) ?? sorted.FirstOrDefault(m => m.Device == a.Monitor);
            if (m0 == null) return "Missing monitor";
            if (sorted.Count == 1) return "Screen";
            int i = sorted.IndexOf(m0);
            if (sorted.Count == 3) return new[] { "Left", "Middle", "Right" }[i];
            if (sorted.Count == 2) return i == 0 ? "Left" : "Right";
            return "Monitor " + (i + 1);
        }

        UIElement AppRow(Workspace ws, WsApp a, List<Workspaces.MonitorInfo> mons, Dictionary<string, string> profiles)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            foreach (var w in new[] { 26.0, 26, -1, 150, 110, 30 })
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });

            var launch = new CheckBox { IsChecked = a.Launch, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Open this app if it has no window" };
            launch.Checked += delegate { a.Launch = true; Workspaces.Save(); };
            launch.Unchecked += delegate { a.Launch = false; Workspaces.Save(); };
            g.Children.Add(launch);

            var img = new Image { Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center };
            try { img.Source = IconLoader.LoadSharp(a.LaunchPath, 36); } catch { }
            Grid.SetColumn(img, 1); g.Children.Add(img);

            var nameCol = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
            if (a.IsChrome)
            {
                var combo = new ComboBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
                combo.Items.Add(new ComboBoxItem { Content = "Google Chrome (any profile)", Tag = "" });
                foreach (var kv in profiles) combo.Items.Add(new ComboBoxItem { Content = "Google Chrome: " + kv.Value, Tag = kv.Key });
                string cur = a.ChromeProfile ?? "";
                combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == cur) ?? combo.Items[0];
                combo.SelectionChanged += delegate
                {
                    var it = combo.SelectedItem as ComboBoxItem;
                    if (it == null) return;
                    Workspaces.SetChromeProfile(a, (string)it.Tag);
                    Workspaces.Save();
                };
                nameCol.Children.Add(combo);
            }
            else nameCol.Children.Add(new TextBlock { Text = a.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(a.TitleHint) && a.TitleHint != a.Name)
                nameCol.Children.Add(new TextBlock { Text = a.TitleHint, FontSize = 11, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(nameCol, 2); g.Children.Add(nameCol);

            var where = new TextBlock { Text = MonitorName(a, mons) + " · " + a.W + "×" + a.H, VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray, FontSize = 12 };
            Grid.SetColumn(where, 3); g.Children.Add(where);

            var states = new[] { "Normal", "Maximized", "Minimized" };
            var state = new ComboBox { ItemsSource = states, SelectedItem = states.Contains(a.State) ? a.State : "Normal", VerticalAlignment = VerticalAlignment.Center };
            state.SelectionChanged += delegate { a.State = (string)state.SelectedItem; Workspaces.Save(); };
            Grid.SetColumn(state, 4); g.Children.Add(state);

            var remove = new Button { Content = "", FontFamily = Glyphs.IconFont, FontSize = 11, Foreground = Brushes.DimGray, Width = 24, Height = 24, Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "Remove from this workspace", Cursor = Cursors.Hand };
            remove.Click += delegate { ws.Apps.Remove(a); Workspaces.Save(); Show(ws); };
            Grid.SetColumn(remove, 5); g.Children.Add(remove);

            var row = new Border { Child = g, CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 3, 6, 3), Background = Brushes.Transparent };
            row.MouseEnter += delegate { row.Background = Panel; Border r; if (previewRects.TryGetValue(a, out r)) r.Background = Accent; };
            row.MouseLeave += delegate { row.Background = Brushes.Transparent; Border r; if (previewRects.TryGetValue(a, out r)) r.Background = new SolidColorBrush(Color.FromArgb(0x70, 0x0A, 0x84, 0xFF)); };
            return row;
        }

        /// <summary>Scaled drawing of the monitors with each app's rectangle.</summary>
        UIElement Preview(Workspace ws)
        {
            var mons = Workspaces.CurrentMonitors();
            double minX = mons.Min(m => m.L), minY = mons.Min(m => m.T), maxX = mons.Max(m => m.L + m.W), maxY = mons.Max(m => m.T + m.H);
            double width = 620, scale = Math.Min(width / (maxX - minX), 210 / (maxY - minY));
            var canvas = new Canvas { Width = (maxX - minX) * scale, Height = (maxY - minY) * scale, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            foreach (var m in mons)
            {
                var b = new Border { Width = m.W * scale - 2, Height = m.H * scale - 2, Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x30)), CornerRadius = new CornerRadius(4) };
                Canvas.SetLeft(b, (m.L - minX) * scale + 1); Canvas.SetTop(b, (m.T - minY) * scale + 1);
                canvas.Children.Add(b);
            }
            foreach (var a in ws.Apps.Where(x => x.State != "Minimized"))
            {
                int x, y, w, h;
                if (a.State == "Maximized")
                {
                    var m = mons.FirstOrDefault(mm => mm.L == a.MonL && mm.T == a.MonT) ?? mons.First();
                    x = m.L; y = m.T; w = m.W; h = m.H;
                }
                else Workspaces.TargetRect(a, out x, out y, out w, out h);
                var label = new TextBlock { Text = a.Name, Foreground = Brushes.White, FontSize = 10, Margin = new Thickness(4, 2, 4, 2), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap };
                var r = new Border
                {
                    Width = Math.Max(4, w * scale - 3), Height = Math.Max(4, h * scale - 3), CornerRadius = new CornerRadius(3), Child = label, ToolTip = a.Name,
                    Background = new SolidColorBrush(Color.FromArgb(0x70, 0x0A, 0x84, 0xFF)), BorderBrush = new SolidColorBrush(Color.FromArgb(0xD0, 0x6C, 0xB4, 0xFF)), BorderThickness = new Thickness(1)
                };
                Canvas.SetLeft(r, (x - minX) * scale + 1.5); Canvas.SetTop(r, (y - minY) * scale + 1.5);
                canvas.Children.Add(r);
                previewRects[a] = r;
            }
            return canvas;
        }
    }

    /// <summary>Small notice under the menu bar that fades away (workspace results).</summary>
    public static class Toast
    {
        static Window current;

        public static void Show(string title, string text, List<string> problems)
        {
            if (current != null) current.Close();
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = Theme.Fg });
            body.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = Theme.FgAlpha(0xC0), Margin = new Thickness(0, 2, 0, 0) });
            foreach (var p in problems.Take(6))
                body.Children.Add(new TextBlock { Text = "• " + p, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)), TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 2, 0, 0) });
            var w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
                ShowActivated = false, SizeToContent = SizeToContent.WidthAndHeight, FontFamily = Theme.Font, ResizeMode = ResizeMode.NoResize,
                Content = new Border
                {
                    Child = body, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(12), CornerRadius = new CornerRadius(12), MinWidth = 240,
                    Background = Theme.PopupBg, BorderBrush = Theme.FgAlpha(0x30), BorderThickness = new Thickness(1),
                    Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 4, Opacity = 0.3 }
                }
            };
            w.MouseLeftButtonUp += delegate { w.Close(); };
            w.Left = -20000; w.Top = -20000;
            w.Show();
            w.UpdateLayout();
            w.Left = (SystemParameters.PrimaryScreenWidth - w.ActualWidth) / 2;
            w.Top = 34;
            current = w;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(problems.Count > 0 ? 9 : 4) };
            t.Tick += delegate { t.Stop(); if (current == w) current = null; w.Close(); };
            t.Start();
        }
    }
}
