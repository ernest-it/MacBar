using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace MacBar
{
    public class SettingsWindow : Window
    {
        readonly Settings s;
        readonly StackPanel body = new StackPanel { Margin = new Thickness(20, 12, 20, 20) };
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public SettingsWindow(Settings settings)
        {
            s = settings;
            Title = "MacBar Settings";
            Width = 460; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = Theme.Font; FontSize = 13;
            Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
            Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 760 };
            Build();
        }

        static void Apply() { App.Instance.ApplySettings(); }

        TextBlock Header(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };
            body.Children.Add(t);
            return t;
        }

        void Row(string label, UIElement control)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(l);
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            body.Children.Add(g);
        }

        CheckBox Check(string label, bool value, Action<bool> set)
        {
            var c = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 4, 0, 4) };
            c.Checked += delegate { set(true); s.Save(); Apply(); };
            c.Unchecked += delegate { set(false); s.Save(); Apply(); };
            body.Children.Add(c);
            return c;
        }

        UIElement SliderWithValue(double min, double max, double value, string suffix, Action<int> set)
        {
            var dp = new DockPanel();
            var val = new TextBlock { Width = 48, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Text = Math.Round(value) + suffix };
            DockPanel.SetDock(val, Dock.Right);
            var sl = new Slider { Minimum = min, Maximum = max, Value = value, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
            sl.ValueChanged += delegate { val.Text = Math.Round(sl.Value) + suffix; set((int)Math.Round(sl.Value)); s.Save(); Apply(); };
            dp.Children.Add(val); dp.Children.Add(sl);
            return dp;
        }

        ComboBox Combo(string[] items, int index, Action<int> set)
        {
            var cb = new ComboBox { ItemsSource = items, SelectedIndex = index, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
            cb.SelectionChanged += delegate { set(cb.SelectedIndex); s.Save(); Apply(); };
            return cb;
        }

        void Build()
        {
            Header("Appearance").Margin = new Thickness(0, 0, 0, 6);
            var themes = new[] { "Auto", "Light", "Dark" };
            Row("Theme", Combo(themes, Math.Max(0, Array.IndexOf(themes, s.Theme)), i => s.Theme = themes[i]));
            Row("Bar opacity", SliderWithValue(0, 100, s.Opacity, "%", v => s.Opacity = v));
            Row("Blur", Combo(new[] { "None", "Light", "Strong" }, s.Blur, i => s.Blur = i));
            body.Children.Add(new TextBlock { Text = "Tip: opacity 0% with blur None makes the bars fully transparent.", Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });

            Header("Dock");
            Row("Icon size", SliderWithValue(32, 72, s.IconSize, " px", v => s.IconSize = v));
            Row("Hide the dock", Combo(new[] { "Never", "Always (like macOS)", "When a window overlaps it" }, s.DockAutoHide, i => s.DockAutoHide = i));
            Check("Keep maximized windows above the dock (when it isn't hidden)", s.DockReservesSpace, v => s.DockReservesSpace = v);

            Header("Menu bar");
            Check("Keep windows and desktop icons below the menu bar", s.MenuBarReservesSpace, v => s.MenuBarReservesSpace = v);
            Check("Show tray (background app) icons", s.ShowTrayIcons, v => s.ShowTrayIcons = v);
            Check("Show battery percentage", s.ShowBatteryPercent, v => s.ShowBatteryPercent = v);

            Header("Weather");
            var current = new TextBlock { Text = s.HasLocation ? "Location: " + s.WeatherLocation : "No location set — the weather item stays hidden until you pick one.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            body.Children.Add(current);
            var searchRow = new DockPanel();
            var go = new Button { Content = "Search", Padding = new Thickness(12, 2, 12, 2), Margin = new Thickness(6, 0, 0, 0), IsDefault = true };
            DockPanel.SetDock(go, Dock.Right);
            var box = new TextBox { Padding = new Thickness(4, 3, 4, 3) };
            searchRow.Children.Add(go); searchRow.Children.Add(box);
            body.Children.Add(searchRow);
            var results = new ListBox { Height = 120, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            body.Children.Add(results);
            var hint = new TextBlock { Foreground = Brushes.Gray, FontSize = 12, Margin = new Thickness(0, 4, 0, 0), Text = "Type a city and press Search, then pick a result." };
            body.Children.Add(hint);
            go.Click += async delegate
            {
                if (string.IsNullOrWhiteSpace(box.Text)) return;
                hint.Text = "Searching…"; go.IsEnabled = false;
                try
                {
                    List<GeoResult> r = await WeatherService.Search(box.Text);
                    results.ItemsSource = r;
                    results.Visibility = r.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                    hint.Text = r.Count > 0 ? "Pick your location:" : "No places found.";
                }
                catch (Exception ex) { hint.Text = "Search failed: " + ex.Message; }
                go.IsEnabled = true;
            };
            results.SelectionChanged += delegate
            {
                var g = results.SelectedItem as GeoResult;
                if (g == null) return;
                s.HasLocation = true; s.WeatherLocation = g.Label; s.Latitude = g.Lat; s.Longitude = g.Lon;
                s.Save();
                current.Text = "Location: " + g.Label;
                hint.Text = "Saved. Loading weather…";
                App.Instance.Weather.LocationChanged();
            };
            var units = new StackPanel { Orientation = Orientation.Horizontal };
            var f = new RadioButton { Content = "Fahrenheit (°F)", IsChecked = s.Fahrenheit, Margin = new Thickness(0, 0, 16, 0), GroupName = "u" };
            var c = new RadioButton { Content = "Celsius (°C)", IsChecked = !s.Fahrenheit, GroupName = "u" };
            f.Checked += delegate { s.Fahrenheit = true; s.Save(); Apply(); };
            c.Checked += delegate { s.Fahrenheit = false; s.Save(); Apply(); };
            units.Children.Add(f); units.Children.Add(c);
            Row("Units", units);
            var clear = new Button { Content = "Clear location", Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
            clear.Click += delegate
            {
                s.HasLocation = false; s.WeatherLocation = ""; s.Save();
                current.Text = "No location set — the weather item stays hidden until you pick one.";
                App.Instance.Weather.LocationChanged();
            };
            body.Children.Add(clear);

            Header("General");
            Check("Start MacBar when I sign in", IsStartupEnabled(), SetStartup);
            var restore = new Button { Content = "Quit MacBar and restore the Windows taskbar", Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            restore.Click += delegate { App.Instance.Quit(); };
            body.Children.Add(restore);
            body.Children.Add(new TextBlock { Text = "Weather data by Open-Meteo.com (CC BY 4.0).", Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 14, 0, 0) });
        }

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
