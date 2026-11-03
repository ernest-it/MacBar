using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MacBar
{
    /// <summary>Small vector icons for the status bar, drawn in the current foreground color.</summary>
    public static class Glyphs
    {
        static Path Stroke(string data, Brush b, double t)
        {
            return new Path
            {
                Data = Geometry.Parse(data), Stroke = b, StrokeThickness = t,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round
            };
        }

        static Path Fill(string data, Brush b) { return new Path { Data = Geometry.Parse(data), Fill = b }; }

        static Canvas Box(double w, double h) { return new Canvas { Width = w, Height = h, SnapsToDevicePixels = true }; }

        /// <summary>Wi-Fi fan. bars: 0-3 active arcs. Inactive parts are drawn faint.</summary>
        public static FrameworkElement Wifi(int bars, bool connected, Brush color = null)
        {
            Brush on = color ?? Theme.Fg;
            Brush off = Theme.FgAlpha(0x50);
            if (color != null) { off = color.Clone(); off.Opacity = 0.35; }
            var c = Box(18, 14);
            c.Children.Add(Fill("M7.4,12.2 A1.6,1.6 0 1 1 10.6,12.2 A1.6,1.6 0 1 1 7.4,12.2 Z", connected ? on : off));
            c.Children.Add(Stroke("M5.82,9.32 A4.5,4.5 0 0 1 12.18,9.32", connected && bars >= 1 ? on : off, 1.9));
            c.Children.Add(Stroke("M3.34,6.84 A8,8 0 0 1 14.66,6.84", connected && bars >= 2 ? on : off, 1.9));
            c.Children.Add(Stroke("M0.95,4.45 A11.4,11.4 0 0 1 17.05,4.45", connected && bars >= 3 ? on : off, 1.9));
            if (!connected) c.Children.Add(Stroke("M2,1 L16,13", on, 1.4));
            return c;
        }

        public static FrameworkElement Ethernet()
        {
            var b = Theme.Fg;
            var c = Box(16, 14);
            c.Children.Add(Stroke("M2.5,2.5 H13.5 V9 H2.5 Z", b, 1.4));
            c.Children.Add(Stroke("M5.5,9 V11.5 H10.5 V9 M5,5 V6.5 M7,5 V6.5 M9,5 V6.5 M11,5 V6.5", b, 1.2));
            return c;
        }

        public static FrameworkElement Speaker(int waves, bool muted, Brush color = null)
        {
            Brush b = color ?? Theme.Fg;
            var c = Box(21, 14);
            c.Children.Add(Fill("M1.5,4.8 H4.6 L8.8,1.2 V12.8 L4.6,9.2 H1.5 Z", b));
            if (muted)
            {
                c.Children.Add(Stroke("M12,4.6 L16.8,9.4 M16.8,4.6 L12,9.4", b, 1.6));
                return c;
            }
            Brush faint = Theme.FgAlpha(0x45);
            if (color != null) { faint = color.Clone(); faint.Opacity = 0.3; }
            c.Children.Add(Stroke("M11.2,4.6 Q12.8,7 11.2,9.4", waves >= 1 ? b : faint, 1.5));
            c.Children.Add(Stroke("M13.6,2.8 Q16.4,7 13.6,11.2", waves >= 2 ? b : faint, 1.5));
            c.Children.Add(Stroke("M16,1 Q20,7 16,13", waves >= 3 ? b : faint, 1.5));
            return c;
        }

        public static FrameworkElement Battery(int pct, bool charging)
        {
            var b = Theme.Fg;
            var c = Box(26, 13);
            c.Children.Add(new Rectangle { Width = 22, Height = 12, RadiusX = 3.2, RadiusY = 3.2, Stroke = Theme.FgAlpha(0x90), StrokeThickness = 1, Margin = new Thickness(0, 0.5, 0, 0) });
            c.Children.Add(new Rectangle { Width = 1.6, Height = 4.5, RadiusX = 0.8, RadiusY = 0.8, Fill = Theme.FgAlpha(0x90), Margin = new Thickness(23, 4.25, 0, 0) });
            double w = Math.Max(1.5, 18 * Math.Max(0, Math.Min(100, pct)) / 100.0);
            Brush fill = (!charging && pct <= 20) ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)) : charging ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)) : b;
            c.Children.Add(new Rectangle { Width = w, Height = 8, RadiusX = 1.6, RadiusY = 1.6, Fill = fill, Margin = new Thickness(2, 2.5, 0, 0) });
            if (charging)
            {
                var bolt = Fill("M12.6,1.4 L7.6,7.2 H10.6 L9.6,11.6 L14.6,5.8 H11.6 Z", b);
                bolt.Stroke = Theme.Dark ? Brushes.Black : Brushes.White;
                bolt.StrokeThickness = 0.8;
                c.Children.Add(bolt);
            }
            return c;
        }

        public static FrameworkElement Logo()
        {
            var b = Theme.Fg;
            var c = Box(14, 14);
            c.Children.Add(Fill("M0.5,0.5 H6.6 V6.6 H0.5 Z M7.4,0.5 H13.5 V6.6 H7.4 Z M0.5,7.4 H6.6 V13.5 H0.5 Z M7.4,7.4 H13.5 V13.5 H7.4 Z", b));
            return c;
        }
    }
}
