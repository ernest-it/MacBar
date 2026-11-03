using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacBar
{
    /// <summary>
    /// "MacBar.exe --demo": the real UI with made-up content, for screenshots. Generic app icons in the
    /// dock and app page, no tray icons, a placeholder Wi-Fi name, a fake song and weather, and a plain
    /// gradient wallpaper over the real desktop. Settings are never written in demo mode.
    /// </summary>
    public static class Demo
    {
        public static bool On;

        // name, glyph, top color, bottom color
        static readonly string[][] AppDefs =
        {
            new[] { "Files", "", "#5AC8FA", "#0A84FF" }, new[] { "Browser", "", "#64D2FF", "#0060DF" },
            new[] { "Mail", "", "#70B7FF", "#1E6FFF" }, new[] { "Messages", "", "#6EE07A", "#21B33B" },
            new[] { "Music", "", "#FF7A8A", "#FA2D48" }, new[] { "Photos", "", "#FFD36E", "#FF9500" },
            new[] { "Calendar", "", "#FF8A80", "#FF3B30" }, new[] { "Notes", "", "#FFE58A", "#FFC300" },
            new[] { "Terminal", "", "#4A4A50", "#1C1C1E" }, new[] { "Settings", "", "#A6A6AE", "#636366" },
            new[] { "Maps", "", "#7BE495", "#30B0C7" }, new[] { "Calculator", "", "#FFB340", "#FF7A00" },
            new[] { "Camera", "", "#8E8E93", "#3A3A3C" }, new[] { "Videos", "", "#5E9BFF", "#3046D9" },
            new[] { "Podcasts", "", "#D58BFF", "#8E3CF7" }, new[] { "Books", "", "#FFB066", "#FF6A00" },
            new[] { "Clock", "", "#3A3A3C", "#000000" }, new[] { "Store", "", "#66B8FF", "#007AFF" },
            new[] { "Code Editor", "", "#4FC3F7", "#0277BD" }, new[] { "Chat", "", "#9C8CFF", "#5856D6" },
            new[] { "Games", "", "#FF6FA8", "#E0245E" }, new[] { "Cloud Drive", "", "#8AD4FF", "#3AA0FF" },
            new[] { "Paint", "", "#FF9AE0", "#C644FC" }, new[] { "Contacts", "", "#C7B299", "#8C7356" },
            new[] { "Health", "", "#FF8FA3", "#FF2D55" }, new[] { "Wallet", "", "#4A4A50", "#111111" },
            new[] { "Documents", "", "#7FA7FF", "#2D5BD8" }, new[] { "Library", "", "#B39DDB", "#6A4FB3" },
            new[] { "Home", "", "#FFC46B", "#FF8C1A" }, new[] { "Learn", "", "#80E0C8", "#14A38B" },
            new[] { "Weather", "", "#7FC8FF", "#2F7DF6" }, new[] { "Recorder", "", "#FF8A80", "#D50000" },
            new[] { "Security", "", "#8FE3A8", "#28A745" }, new[] { "Reader", "", "#E0E0E0", "#9E9E9E" },
            new[] { "Tips", "", "#FFE066", "#FFB300" }, new[] { "Translate", "", "#7AB8FF", "#2962FF" },
            new[] { "Stocks", "", "#3A3A3C", "#1C1C1E" }, new[] { "Voice Memos", "", "#5A5A60", "#2C2C2E" },
            new[] { "Remote", "", "#8FA3FF", "#3D5AFE" }, new[] { "Print Center", "", "#B0BEC5", "#607D8B" },
        };

        static readonly string[] DockApps = { "Files", "Browser", "Mail", "Messages", "Music", "Photos", "Calendar", "Notes", "Terminal", "Code Editor", "Settings" };

        public static void Apply(Settings s)
        {
            s.ShowTrayIcons = false;
            s.PinsImported = true;
            s.HasLocation = true; s.WeatherLocation = "San Francisco"; s.Latitude = 37.77; s.Longitude = -122.42;
            s.Pinned = DockApps.Select(n => new PinnedApp { Name = n, Target = "demo:" + n }).ToList();
        }

        public static List<AppEntry> Apps()
        {
            return AppDefs.Select(d => new AppEntry { Name = d[0], Path = "demo:" + d[0] })
                          .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static bool IsDemoTarget(string target) { return target != null && target.Contains("demo:"); }

        /// <summary>Generic app icon: rounded gradient tile with a white glyph, drawn at an exact pixel size.</summary>
        public static ImageSource Icon(string target, int px)
        {
            string name = target.Substring(target.IndexOf("demo:") + 5);
            var def = AppDefs.FirstOrDefault(d => d[0] == name) ?? AppDefs[0];
            return Tile(def[1], Hex(def[2]), Hex(def[3]), px);
        }

        static Color Hex(string s) { return (Color)ColorConverter.ConvertFromString(s); }

        public static BitmapSource Tile(string glyph, Color top, Color bottom, int px)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                double r = px * 0.225, inset = px * 0.04;
                var rect = new Rect(inset, inset, px - inset * 2, px - inset * 2);
                var fill = new LinearGradientBrush(top, bottom, 90);
                dc.DrawRoundedRectangle(fill, new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), Math.Max(1, px / 64.0)), rect, r, r);
                var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(Glyphs.IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), px * 0.46, Brushes.White, 1.0);
                dc.DrawText(ft, new Point((px - ft.Width) / 2, (px - ft.Height) / 2));
            }
            var bmp = new RenderTargetBitmap(px, px, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }

        public static WeatherData Weather()
        {
            var days = new List<WeatherDay>();
            int[] codes = { 1, 2, 3, 61, 0, 2 };
            double[] max = { 22, 24, 19, 16, 21, 23 }, min = { 13, 14, 12, 11, 12, 14 };
            for (int i = 0; i < 6; i++)
                days.Add(new WeatherDay { Date = DateTime.Today.AddDays(i).ToString("yyyy-MM-dd"), Code = codes[i], MaxC = max[i], MinC = min[i] });
            return new WeatherData
            {
                TempC = 21, FeelsC = 21, Humidity = 58, WindKmh = 11, Code = 1, IsDay = true, FetchedUtc = DateTime.UtcNow,
                Location = "San Francisco", Lat = 37.77, Lon = -122.42, Days = days
            };
        }

        static readonly DateTime started = DateTime.Now;
        static ImageSource art;
        public static bool Paused;

        public static NowPlaying Track()
        {
            if (art == null) art = Tile("", Hex("#FF9A5A"), Hex("#C2185B"), 256);
            const double length = 214;
            double pos = (63 + (DateTime.Now - started).TotalSeconds) % length;
            return new NowPlaying
            {
                Title = "Golden Hour Drive", Artist = "Neon Coast", AppId = "Music", Playing = !Paused, Art = art,
                Position = pos, Duration = length, PositionAt = DateTime.UtcNow, CanSeek = true
            };
        }

        /// <summary>Plain gradient "wallpaper" above the real desktop and windows, below MacBar's bars.</summary>
        public static void ShowBackdrop()
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                Left = 0, Top = 0, Width = SystemParameters.PrimaryScreenWidth, Height = SystemParameters.PrimaryScreenHeight,
                Background = new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Hex("#1D2B64"), 0), new GradientStop(Hex("#5B3A8C"), 0.45), new GradientStop(Hex("#E0716B"), 0.8), new GradientStop(Hex("#F8B26A"), 1)
                }, new Point(0, 0), new Point(1, 1))
            };
            w.SourceInitialized += delegate { Native.AddExStyle(new WindowInteropHelper(w).Handle, Native.WS_EX_TOOLWINDOW); };
            w.Show();
        }
    }
}
