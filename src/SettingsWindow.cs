using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace MacBar
{
    /// <summary>
    /// MacBar Settings, laid out like a GNOME/libadwaita preferences window: page tabs at the top,
    /// grouped rounded cards with one setting per row (title, subtitle, control on the right).
    /// Follows MacBar's light/dark theme.
    /// </summary>
    public class SettingsWindow : Window
    {
        readonly Settings s;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static readonly string[] Pages = { "Menu bar", "Dock", "Weather", "General" };
        static readonly string[] PageIcons = { "", "", "", "" };
        string currentPage = "Menu bar";
        StackPanel page;
        ScrollViewer scroller;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public SettingsWindow(Settings settings)
        {
            s = settings;
            Title = "MacBar Settings";
            Width = 680; Height = 760; MinWidth = 520; MinHeight = 420;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Theme.Font; FontSize = 13;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            SourceInitialized += delegate { DarkTitleBar(); };
            Resources.MergedDictionaries.Add(ScrollBarStyle());
            BuildShell();
        }

        // ---------- colors ----------

        static SolidColorBrush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
        static Brush WindowBg { get { return Theme.Dark ? B(0x24, 0x24, 0x26) : B(0xF6, 0xF6, 0xF7); } }
        static Brush CardBg { get { return Theme.Dark ? B(0x31, 0x31, 0x34) : Brushes.White; } }
        static Brush CardLine { get { return Theme.Dark ? B(0x3C, 0x3C, 0x40) : B(0xE4, 0xE4, 0xE7); } }
        static Brush Sub { get { return Theme.FgAlpha(0x8C); } }
        static Brush AccentBrush { get { var b = new SolidColorBrush(Theme.Accent); b.Freeze(); return b; } }

        /// <summary>A slim, rounded scrollbar thumb with no arrows or track (the default one is the old gray bar).</summary>
        static ResourceDictionary ScrollBarStyle()
        {
            const string xaml = @"<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
 <Style TargetType='ScrollBar'>
  <Setter Property='Width' Value='10'/>
  <Setter Property='MinWidth' Value='10'/>
  <Setter Property='Background' Value='Transparent'/>
  <Setter Property='Template'><Setter.Value>
   <ControlTemplate TargetType='ScrollBar'>
    <Track x:Name='PART_Track' IsDirectionReversed='True' Margin='2,4,2,4'>
     <Track.Thumb>
      <Thumb>
       <Thumb.Template>
        <ControlTemplate TargetType='Thumb'>
         <Border x:Name='T' CornerRadius='3' Background='{FG}' Opacity='0.28'/>
         <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='T' Property='Opacity' Value='0.5'/></Trigger>
          <Trigger Property='IsDragging' Value='True'><Setter TargetName='T' Property='Opacity' Value='0.6'/></Trigger>
         </ControlTemplate.Triggers>
        </ControlTemplate>
       </Thumb.Template>
      </Thumb>
     </Track.Thumb>
    </Track>
   </ControlTemplate>
  </Setter.Value></Setter>
 </Style>
</ResourceDictionary>";
            return (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml.Replace("{FG}", Theme.Dark ? "White" : "Black"));
        }

        void DarkTitleBar()
        {
            try
            {
                IntPtr h = new WindowInteropHelper(this).Handle;
                int on = Theme.Dark ? 1 : 0;
                DwmSetWindowAttribute(h, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, 4);
            }
            catch { }
        }

        static void Apply() { App.Instance.ApplySettings(); }
        void Save() { s.Save(); Apply(); }

        // ---------- shell: tabs + scrolling page ----------

        void BuildShell()
        {
            Background = WindowBg;
            Foreground = Theme.Fg;
            var root = new DockPanel();

            var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 6) };
            for (int i = 0; i < Pages.Length; i++)
            {
                string name = Pages[i];
                bool sel = name == currentPage;
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new TextBlock { Text = PageIcons[i], FontFamily = Glyphs.IconFont, FontSize = 15, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
                content.Children.Add(new TextBlock { Text = name, FontWeight = sel ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
                var tab = new Border
                {
                    Child = content, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(3, 0, 3, 0),
                    CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand,
                    Background = sel ? Theme.FgAlpha(Theme.Dark ? (byte)0x26 : (byte)0x14) : Brushes.Transparent,
                };
                if (!sel)
                {
                    tab.MouseEnter += delegate { tab.Background = Theme.FgAlpha(0x0E); };
                    tab.MouseLeave += delegate { tab.Background = Brushes.Transparent; };
                }
                tab.MouseLeftButtonUp += delegate { currentPage = name; BuildShell(); };
                tabs.Children.Add(tab);
            }
            DockPanel.SetDock(tabs, Dock.Top);
            root.Children.Add(tabs);

            page = new StackPanel { Margin = new Thickness(24, 4, 24, 28), MaxWidth = 580 };
            scroller = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            root.Children.Add(scroller);
            Content = root;

            switch (currentPage)
            {
                case "Menu bar": BuildMenuBar(); break;
                case "Dock": BuildDock(); break;
                case "Weather": BuildWeather(); break;
                default: BuildGeneral(); break;
            }
        }

        // ---------- building blocks ----------

        /// <summary>A titled group with a rounded card; returns the card's row stack.</summary>
        StackPanel Group(string title, string description = null)
        {
            var g = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            if (title != null) g.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 0, 0, description == null ? 8 : 2) });
            if (description != null) g.Children.Add(new TextBlock { Text = description, Foreground = Sub, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 8) });
            var rows = new StackPanel();
            g.Children.Add(new Border
            {
                Background = CardBg, CornerRadius = new CornerRadius(12), BorderBrush = CardLine, BorderThickness = new Thickness(1),
                Child = rows, SnapsToDevicePixels = true,
            });
            page.Children.Add(g);
            return rows;
        }

        /// <summary>One row: title (+ subtitle) on the left, an optional control on the right.</summary>
        Border Row(StackPanel card, string title, string subtitle, FrameworkElement control, FrameworkElement leading = null)
        {
            var grid = new Grid { MinHeight = 34 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (leading != null)
            {
                leading.VerticalAlignment = VerticalAlignment.Center;
                leading.Margin = new Thickness(0, 0, 12, 0);
                grid.Children.Add(leading);
            }
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, FontSize = 13.5, TextWrapping = TextWrapping.Wrap });
            if (!string.IsNullOrEmpty(subtitle))
                text.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = Sub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            if (control != null)
            {
                control.VerticalAlignment = VerticalAlignment.Center;
                control.Margin = new Thickness(16, 0, 0, 0);
                Grid.SetColumn(control, 2);
                grid.Children.Add(control);
            }
            var row = new Border
            {
                Child = grid, Padding = new Thickness(16, 11, 14, 11), Background = Brushes.Transparent,
                BorderBrush = CardLine, BorderThickness = new Thickness(0, card.Children.Count == 0 ? 0 : 1, 0, 0),
            };
            card.Children.Add(row);
            return row;
        }

        void Toggle(StackPanel card, string title, string subtitle, bool value, Action<bool> set)
        {
            var sw = new ToggleSwitch(value);
            sw.Toggled += on => { set(on); Save(); };
            var row = Row(card, title, subtitle, sw);
            row.Cursor = Cursors.Hand;
            // Clicking anywhere on the row flips the switch.
            row.MouseLeftButtonUp += delegate (object o, MouseButtonEventArgs e)
            {
                if (e.Handled) return;
                sw.IsOn = !sw.IsOn;
                set(sw.IsOn); Save();
            };
        }

        /// <summary>A compact dropdown button; the choices open in MacBar's own menu style.</summary>
        FrameworkElement DropDown(string[] items, int index, Action<int> set)
        {
            var label = new TextBlock { Text = items[Math.Max(0, Math.Min(items.Length - 1, index))], VerticalAlignment = VerticalAlignment.Center };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(label);
            content.Children.Add(new TextBlock { Text = "", FontFamily = Glyphs.IconFont, FontSize = 10, Margin = new Thickness(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Sub });
            var btn = new Border
            {
                Child = content, Padding = new Thickness(12, 6, 10, 6), CornerRadius = new CornerRadius(8), Cursor = Cursors.Hand,
                Background = Theme.FgAlpha(0x10), MinWidth = 120,
            };
            btn.MouseEnter += delegate { btn.Background = Theme.FgAlpha(0x1C); };
            btn.MouseLeave += delegate { btn.Background = Theme.FgAlpha(0x10); };
            btn.MouseLeftButtonUp += delegate (object o, MouseButtonEventArgs e)
            {
                e.Handled = true;
                var menu = new ContextMenu { PlacementTarget = btn, Placement = PlacementMode.Bottom, VerticalOffset = 4 };
                for (int i = 0; i < items.Length; i++)
                {
                    int k = i;
                    var mi = new MenuItem { Header = items[i], FontWeight = items[i] == label.Text ? FontWeights.SemiBold : FontWeights.Normal };
                    mi.Click += delegate { label.Text = items[k]; set(k); };
                    menu.Items.Add(mi);
                }
                menu.IsOpen = true;
            };
            return btn;
        }

        FrameworkElement SliderControl(double min, double max, double value, string suffix, Action<int> set)
        {
            var dp = new DockPanel { Width = 230 };
            var val = new TextBlock { Width = 52, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = Sub, Text = Math.Round(value) + suffix };
            DockPanel.SetDock(val, Dock.Right);
            var sl = new MiniSlider { Value = (value - min) / (max - min), VerticalAlignment = VerticalAlignment.Center };
            int last = (int)Math.Round(value);
            sl.ValueChanged += v =>
            {
                int n = (int)Math.Round(min + v * (max - min));
                if (n == last) return;
                last = n;
                val.Text = n + suffix;
                set(n); Save();
            };
            dp.Children.Add(val); dp.Children.Add(sl);
            return dp;
        }

        Border Button(string text, Action click, bool accent = false, bool destructive = false)
        {
            Brush normal = accent ? AccentBrush : Theme.FgAlpha(0x12);
            Brush hover = accent ? (Brush)B(0x2A, 0x93, 0xFF) : Theme.FgAlpha(0x20);
            var t = new TextBlock
            {
                Text = text, FontWeight = FontWeights.SemiBold,
                Foreground = accent ? Brushes.White : destructive ? (Brush)B(0xE0, 0x3E, 0x3E) : Theme.Fg,
            };
            var b = new Border { Child = t, Padding = new Thickness(14, 6, 14, 6), CornerRadius = new CornerRadius(8), Background = normal, Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left };
            b.MouseEnter += delegate { b.Background = hover; };
            b.MouseLeave += delegate { b.Background = normal; };
            b.MouseLeftButtonUp += delegate (object o, MouseButtonEventArgs e) { e.Handled = true; click(); };
            return b;
        }

        TextBlock Note(string text)
        {
            var t = new TextBlock { Text = text, Foreground = Sub, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 0, 0) };
            page.Children.Add(t);
            return t;
        }

        // ---------- pages ----------

        void BuildMenuBar()
        {
            var look = Group("Look", "Applies to the menu bar and the dock.");
            var themes = new[] { "Auto", "Light", "Dark" };
            Row(look, "Theme", "Auto follows Windows' light or dark mode", DropDown(themes, Math.Max(0, Array.IndexOf(themes, s.Theme)), i =>
            {
                s.Theme = themes[i]; Save();
                Resources.MergedDictionaries.Clear(); Resources.MergedDictionaries.Add(ScrollBarStyle());
                BuildShell(); DarkTitleBar(); // recolor this window too
            }));
            Row(look, "Background opacity", "0% with blur off makes the bars fully transparent", SliderControl(0, 100, s.Opacity, "%", v => s.Opacity = v));
            Row(look, "Blur", null, DropDown(new[] { "None", "Light", "Strong" }, s.Blur, i => { s.Blur = i; Save(); }));

            var items = Group("Items");
            Toggle(items, "Background app icons", "Tray icons from apps running in the background", s.ShowTrayIcons, v => { s.ShowTrayIcons = v; BuildShell(); });
            Toggle(items, "Battery percentage", "Shown on laptops", s.ShowBatteryPercent, v => s.ShowBatteryPercent = v);
            Toggle(items, "OpenRGB lighting profiles", "Shown while OpenRGB runs with its SDK server on", s.ShowLighting, v => { s.ShowLighting = v; if (v) OpenRgb.Refresh(Dispatcher); });

            var behave = Group("Behavior");
            Toggle(behave, "Keep windows below the menu bar", "Maximized windows and desktop icons stay out from under it", s.MenuBarReservesSpace, v => s.MenuBarReservesSpace = v);

            // One switch per background-app icon currently in the menu bar (e.g. hide Windows Security).
            var icons = App.Instance.Tray.Icons.Where(i => !i.Hidden && i.Icon != null && !TopBar.Redundant(i))
                                               .GroupBy(i => i.Key).Select(g => g.First()).OrderBy(i => i.Label).ToList();
            if (s.ShowTrayIcons && icons.Count > 0)
            {
                var list = Group("Menu bar icons", "Choose which background app icons appear in the menu bar.");
                foreach (var ic in icons)
                {
                    string key = ic.Key;
                    var img = new Image { Source = ic.Icon, Width = 18, Height = 18 };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    var sw = new ToggleSwitch(!s.HiddenTrayIcons.Contains(key));
                    sw.Toggled += on =>
                    {
                        if (on) s.HiddenTrayIcons.Remove(key);
                        else if (!s.HiddenTrayIcons.Contains(key)) s.HiddenTrayIcons.Add(key);
                        Save();
                    };
                    Row(list, ic.Label, null, sw, img);
                }
            }
        }

        void BuildDock()
        {
            var dock = Group("Dock", "Right-click an app in the dock to keep it or remove it. Drag icons to reorder them.");
            Row(dock, "Hide the dock", null, DropDown(new[] { "Never", "Always (like macOS)", "When a window overlaps it" }, s.DockAutoHide, i => { s.DockAutoHide = i; Save(); }));
            Row(dock, "Icon size", null, SliderControl(32, 72, s.IconSize, " px", v => s.IconSize = v));
            Row(dock, "Hover magnification", "How much an icon grows under the mouse (100% = off)", SliderControl(100, 200, s.DockMagnify, "%", v => s.DockMagnify = v));
            Toggle(dock, "Keep maximized windows above the dock", "Only when the dock never hides", s.DockReservesSpace, v => s.DockReservesSpace = v);
        }

        void BuildWeather()
        {
            var loc = Group("Location", "Weather comes from Open-Meteo.com: no account and no location access. Search for your city.");
            var currentRow = Row(loc, "Current city", s.HasLocation ? s.WeatherLocation : "Not set (the weather item stays hidden until you pick one)",
                s.HasLocation ? Button("Clear", delegate
                {
                    s.HasLocation = false; s.WeatherLocation = ""; s.Save();
                    App.Instance.Weather.LocationChanged();
                    BuildShell();
                }) : null);

            // Search box styled like the rest of the window.
            var box = new TextBox
            {
                BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Theme.Fg, CaretBrush = Theme.Fg,
                FontSize = 13.5, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0),
            };
            var placeholder = new TextBlock { Text = "Search for a city", Foreground = Sub, FontSize = 13.5, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
            box.TextChanged += delegate { placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; };
            var boxGrid = new Grid();
            boxGrid.Children.Add(placeholder); boxGrid.Children.Add(box);
            var search = Button("Search", delegate { }, true);
            var searchRow = new DockPanel();
            DockPanel.SetDock(search, Dock.Right);
            search.Margin = new Thickness(12, 0, 0, 0);
            searchRow.Children.Add(search);
            searchRow.Children.Add(new TextBlock { Text = "", FontFamily = Glyphs.IconFont, Foreground = Sub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
            searchRow.Children.Add(boxGrid);
            loc.Children.Add(new Border { Child = searchRow, Padding = new Thickness(16, 9, 10, 9), BorderBrush = CardLine, BorderThickness = new Thickness(0, 1, 0, 0) });

            var resultsHost = new StackPanel();
            page.Children.Add(resultsHost);
            var hint = Note("Type a city and press Enter.");

            Action run = async delegate
            {
                if (string.IsNullOrWhiteSpace(box.Text)) return;
                hint.Text = "Searching…";
                resultsHost.Children.Clear();
                try
                {
                    List<GeoResult> r = await WeatherService.Search(box.Text);
                    hint.Text = r.Count > 0 ? "Pick your location." : "No places found.";
                    if (r.Count == 0) return;
                    var saved = page;
                    page = resultsHost; // let Group() add the results card here
                    var card = Group(null);
                    page = saved;
                    ((FrameworkElement)card.Parent).Margin = new Thickness(0);
                    foreach (var g in r)
                    {
                        var geo = g;
                        var chevron = new TextBlock { Text = "", FontFamily = Glyphs.IconFont, FontSize = 11, Foreground = Sub };
                        var row = Row(card, geo.Label, null, chevron);
                        row.Cursor = Cursors.Hand;
                        row.MouseEnter += delegate { row.Background = Theme.FgAlpha(0x0A); };
                        row.MouseLeave += delegate { row.Background = Brushes.Transparent; };
                        row.MouseLeftButtonUp += delegate
                        {
                            s.HasLocation = true; s.WeatherLocation = geo.Label; s.Latitude = geo.Lat; s.Longitude = geo.Lon;
                            s.Save();
                            App.Instance.Weather.LocationChanged();
                            BuildShell();
                        };
                    }
                    ((FrameworkElement)card.Parent).Margin = new Thickness(0, 12, 0, 0);
                }
                catch (Exception ex) { hint.Text = "Search failed: " + ex.Message; }
            };
            search.MouseLeftButtonUp += delegate { run(); };
            box.KeyDown += delegate (object o, KeyEventArgs e) { if (e.Key == Key.Enter) run(); };

            var units = Group("Units");
            Row(units, "Temperature", null, DropDown(new[] { "Fahrenheit (°F)", "Celsius (°C)" }, s.Fahrenheit ? 0 : 1, i => { s.Fahrenheit = i == 0; Save(); }));
            Note("Weather data by Open-Meteo.com (CC BY 4.0).");
        }

        void BuildGeneral()
        {
            var startup = Group("Startup");
            Toggle(startup, "Start MacBar when I sign in", null, IsStartupEnabled(), SetStartup);

            var ws = Group("Workspaces", "Save where your windows go across monitors and put them back in one click.");
            int count = Workspaces.All.Count;
            Row(ws, "Workspaces", count == 0 ? "None saved yet" : count + (count == 1 ? " workspace" : " workspaces"),
                Button("Edit Workspaces…", delegate { App.Instance.ShowWorkspaces(); }));

            var about = Group("About");
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            Row(about, "MacBar", "Version " + v.Major + "." + v.Minor + "." + v.Build, Button("GitHub", delegate { ShellUtil.Open("https://github.com/ernest-it/MacBar"); }));
            Row(about, "Quit MacBar", "Puts the Windows taskbar back", Button("Quit", delegate { App.Instance.Quit(); }, false, true));
        }

        // ---------- startup registry ----------

        static bool IsStartupEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("MacBar") != null;
        }

        static void SetStartup(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("MacBar", "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
                else k.DeleteValue("MacBar", false);
            }
        }
    }
}
