using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MacBar
{
    /// <summary>
    /// Status icons. These use Windows 11's "Segoe Fluent Icons" font (falls back to "Segoe MDL2 Assets"
    /// on Windows 10): the glyphs are hinted for 16 px, so they land on whole pixels and stay sharp,
    /// unlike free-form vector paths.
    /// </summary>
    public static class Glyphs
    {
        public static readonly FontFamily IconFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        public const double Size = 16;

        static TextBlock G(string glyph, Brush b, double size)
        {
            var t = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = size, Foreground = b, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true };
            TextOptions.SetTextFormattingMode(t, TextFormattingMode.Display);
            TextOptions.SetTextHintingMode(t, TextHintingMode.Fixed);
            return t;
        }

        /// <summary>Faint full glyph underneath, strength level on top (like the Windows taskbar).</summary>
        static FrameworkElement Layered(string background, string level, Brush on, Brush faint, double size)
        {
            var g = new Grid { VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(G(background, faint, size));
            if (level != null) g.Children.Add(G(level, on, size));
            return g;
        }

        static Brush Faint(Brush color, byte themeAlpha, double opacity)
        {
            if (color == null) return Theme.FgAlpha(themeAlpha);
            var f = color.Clone(); f.Opacity = opacity; return f;
        }

        public static FrameworkElement Wifi(int bars, bool connected, Brush color = null, double size = Size)
        {
            Brush on = color ?? Theme.Fg;
            Brush faint = Faint(color, 0x4C, 0.3);
            if (!connected) return Layered("", null, on, faint, size);
            string level = bars >= 3 ? "" : bars == 2 ? "" : "";
            return Layered("", level, on, faint, size);
        }

        public static FrameworkElement Ethernet() { return G("", Theme.Fg, Size); }

        public static FrameworkElement Speaker(int waves, bool muted, Brush color = null, double size = Size)
        {
            Brush on = color ?? Theme.Fg;
            if (muted) return G("", on, size);
            string level = waves <= 0 ? "" : waves == 1 ? "" : waves == 2 ? "" : "";
            return Layered("", level, on, Faint(color, 0x40, 0.3), size);
        }

        public static FrameworkElement Battery(int pct, bool charging)
        {
            int level = Math.Max(0, Math.Min(10, (int)Math.Round(pct / 10.0)));
            string glyph = charging ? ((char)(0xE85A + Math.Min(8, level))).ToString()
                         : level >= 10 ? "" : ((char)(0xE850 + level)).ToString();
            Brush b = (!charging && pct <= 20) ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) : Theme.Fg;
            return G(glyph, b, 18);
        }

        public static FrameworkElement ControlCenter() { return G("", Theme.Fg, Size); }

        public static FrameworkElement Logo()
        {
            // Integer coordinates so every edge lands on a pixel boundary.
            var c = new Canvas { Width = 13, Height = 13, SnapsToDevicePixels = true };
            c.Children.Add(new Path { Data = Geometry.Parse("M0,0 H6 V6 H0 Z M7,0 H13 V6 H7 Z M0,7 H6 V13 H0 Z M7,7 H13 V13 H7 Z"), Fill = Theme.Fg, SnapsToDevicePixels = true });
            return c;
        }
    }
}
