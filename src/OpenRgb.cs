using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MacBar
{
    /// <summary>
    /// Talks to OpenRGB's SDK server (Settings → SDK Server in OpenRGB, port 6742 on this PC) to list
    /// saved profiles and load one. Each request uses its own short connection on a background thread.
    /// </summary>
    public static class OpenRgb
    {
        const int Port = 6742;
        const uint SetClientName = 50, RequestProtocolVersion = 40, RequestProfileList = 150, RequestLoadProfile = 152;

        public static bool Available { get; private set; }
        public static List<string> Profiles = new List<string>();
        public static event Action Changed;
        static int busy;

        /// <summary>Re-reads the profile list in the background; raises Changed on the UI thread if anything differs.</summary>
        public static void Refresh(Dispatcher ui)
        {
            if (Demo.On || Interlocked.Exchange(ref busy, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<string> list = null;
                try { list = Run(s => ReadProfiles(s)); }
                catch (Exception ex) { App.Trace("openrgb: " + ex.Message); }
                finally { busy = 0; }
                ui.BeginInvoke(new Action(delegate
                {
                    bool ok = list != null;
                    if (ok == Available && (list ?? new List<string>()).SequenceEqual(Profiles)) return;
                    Available = ok;
                    Profiles = list ?? new List<string>();
                    if (Changed != null) Changed();
                }));
            });
        }

        public static void Load(string profile, Dispatcher ui, Action<bool> done)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = false;
                try
                {
                    Run<object>(s =>
                    {
                        Send(s, RequestLoadProfile, Encoding.UTF8.GetBytes(profile + "\0"));
                        Thread.Sleep(150); // let the server read it before we hang up
                        return null;
                    });
                    ok = true;
                }
                catch (Exception ex) { App.Trace("openrgb load failed: " + ex.Message); }
                ui.BeginInvoke(new Action(delegate { done(ok); }));
            });
        }

        static T Run<T>(Func<NetworkStream, T> work)
        {
            using (var c = new TcpClient())
            {
                var ar = c.BeginConnect("127.0.0.1", Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(400) || !c.Connected) throw new IOException("SDK server not reachable");
                c.EndConnect(ar);
                var s = c.GetStream();
                s.ReadTimeout = 2000; s.WriteTimeout = 2000;
                Send(s, SetClientName, Encoding.ASCII.GetBytes("MacBar\0"));
                Send(s, RequestProtocolVersion, BitConverter.GetBytes(4u));
                uint version = BitConverter.ToUInt32(Receive(s, RequestProtocolVersion), 0);
                if (version < 2) throw new IOException("OpenRGB is too old for profiles (protocol " + version + ")");
                return work(s);
            }
        }

        static List<string> ReadProfiles(NetworkStream s)
        {
            Send(s, RequestProfileList, new byte[0]);
            byte[] d = Receive(s, RequestProfileList);
            var list = new List<string>();
            int count = BitConverter.ToUInt16(d, 4), o = 6; // u32 data size, u16 count, then u16 length + name
            for (int i = 0; i < count && o + 2 <= d.Length; i++)
            {
                int len = BitConverter.ToUInt16(d, o); o += 2;
                list.Add(Encoding.UTF8.GetString(d, o, Math.Min(len, d.Length - o)).TrimEnd('\0'));
                o += len;
            }
            return list;
        }

        static void Send(NetworkStream s, uint id, byte[] data)
        {
            var p = new byte[16 + data.Length];
            Encoding.ASCII.GetBytes("ORGB").CopyTo(p, 0);
            BitConverter.GetBytes(0u).CopyTo(p, 4);
            BitConverter.GetBytes(id).CopyTo(p, 8);
            BitConverter.GetBytes((uint)data.Length).CopyTo(p, 12);
            data.CopyTo(p, 16);
            s.Write(p, 0, p.Length);
        }

        /// <summary>Reads packets until one with the wanted id arrives (the server may send "device list updated" in between).</summary>
        static byte[] Receive(NetworkStream s, uint id)
        {
            for (int n = 0; n < 20; n++)
            {
                byte[] h = ReadExactly(s, 16);
                if (Encoding.ASCII.GetString(h, 0, 4) != "ORGB") throw new IOException("unexpected reply");
                uint got = BitConverter.ToUInt32(h, 8), size = BitConverter.ToUInt32(h, 12);
                if (size > 16 * 1024 * 1024) throw new IOException("reply too large");
                byte[] d = ReadExactly(s, (int)size);
                if (got == id) return d;
            }
            throw new IOException("no reply");
        }

        static byte[] ReadExactly(NetworkStream s, int n)
        {
            var b = new byte[n];
            for (int o = 0; o < n; )
            {
                int r = s.Read(b, o, n - o);
                if (r <= 0) throw new IOException("connection closed");
                o += r;
            }
            return b;
        }

        /// <summary>A color for a profile's dot: named colors ("Lavender", "Orange", "mint") or null for an RGB rainbow.</summary>
        public static Color? Swatch(string name)
        {
            string n = name.Trim().ToLowerInvariant();
            if (n == "off" || n == "none" || n == "dark") return Colors.Transparent;
            var extra = new Dictionary<string, string> { { "mint", "#3EB489" }, { "lavender", "#B57EDC" }, { "lilac", "#C8A2C8" }, { "rose", "#FF6F91" }, { "sky", "#5AC8FA" }, { "ice", "#BDEBFF" }, { "warm", "#FFB46B" }, { "cool", "#7FB2FF" } };
            foreach (var word in n.Split(' ', '-', '_'))
            {
                string hex;
                if (extra.TryGetValue(word, out hex)) return (Color)ColorConverter.ConvertFromString(hex);
                try { return (Color)ColorConverter.ConvertFromString(word); } catch { }
            }
            return null;
        }

        /// <summary>OpenRGB's Start menu shortcut, if installed (the running copy is elevated, so its path can't be read).</summary>
        public static string Shortcut()
        {
            foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
            {
                try
                {
                    var f = Directory.EnumerateFiles(dir, "OpenRGB*.lnk", SearchOption.AllDirectories).FirstOrDefault();
                    if (f != null) return f;
                }
                catch { }
            }
            return null;
        }
    }

    /// <summary>Lighting panel: OpenRGB profiles, click one to load it.</summary>
    public class LightingFlyout : Flyout
    {
        readonly Settings s;
        readonly StackPanel list = new StackPanel();
        readonly TextBlock status;

        public LightingFlyout(Settings settings, Action onChange) : base("lighting", 250)
        {
            s = settings;
            Body.Children.Add(Text("Lighting", 13, FontWeights.SemiBold, 1));
            status = Text("OpenRGB profiles", 11, FontWeights.Normal, 0.55);
            status.Margin = new Thickness(0, 0, 0, 4);
            Body.Children.Add(status);
            Body.Children.Add(list);
            Build(onChange);
            string lnk = OpenRgb.Shortcut();
            if (lnk != null)
            {
                Body.Children.Add(Separator());
                Body.Children.Add(Link("Open OpenRGB…", delegate { ShellUtil.Open(lnk); }));
            }
        }

        void Build(Action onChange)
        {
            list.Children.Clear();
            if (OpenRgb.Profiles.Count == 0)
            {
                var t = Text("No saved profiles. Save one in OpenRGB first.", 12, FontWeights.Normal, 0.7);
                t.TextWrapping = TextWrapping.Wrap;
                list.Children.Add(t);
                return;
            }
            foreach (var p in OpenRgb.Profiles)
            {
                string name = p;
                bool current = string.Equals(name, s.OpenRgbProfile, StringComparison.CurrentCultureIgnoreCase);
                var row = new DockPanel();
                row.Children.Add(Dot(name, current));
                if (current)
                {
                    var check = new TextBlock { Text = "", FontFamily = Glyphs.IconFont, FontSize = 12, Foreground = Theme.Fg, VerticalAlignment = VerticalAlignment.Center };
                    DockPanel.SetDock(check, Dock.Right);
                    row.Children.Add(check);
                }
                var label = Text(Pretty(name), 13, current ? FontWeights.SemiBold : FontWeights.Normal, 1);
                label.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(label);
                var host = new Border { Child = row, CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(-6, 1, -6, 1), Background = Brushes.Transparent, Cursor = Cursors.Hand };
                host.MouseEnter += delegate { host.Background = Theme.Hover; };
                host.MouseLeave += delegate { host.Background = Brushes.Transparent; };
                host.MouseLeftButtonUp += delegate
                {
                    status.Text = "Switching to " + Pretty(name) + "…";
                    OpenRgb.Load(name, Dispatcher, delegate (bool ok)
                    {
                        if (!ok) { status.Text = "Couldn't reach OpenRGB. Is its SDK server on?"; return; }
                        s.OpenRgbProfile = name; s.Save();
                        status.Text = "OpenRGB profiles";
                        Build(onChange);
                        onChange();
                    });
                };
                list.Children.Add(host);
            }
        }

        static string Pretty(string name) { return name.Length > 0 && char.IsLower(name[0]) ? char.ToUpper(name[0]) + name.Substring(1) : name; }

        static UIElement Dot(string name, bool current)
        {
            Color? c = OpenRgb.Swatch(name);
            Brush fill;
            if (c == null)
                fill = new LinearGradientBrush(new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0xFF, 0x3B, 0x30), 0), new GradientStop(Color.FromRgb(0xFF, 0xCC, 0x00), 0.33),
                    new GradientStop(Color.FromRgb(0x34, 0xC7, 0x59), 0.66), new GradientStop(Color.FromRgb(0x0A, 0x84, 0xFF), 1)
                }, new Point(0, 0), new Point(1, 1));
            else fill = new SolidColorBrush(c.Value);
            var dot = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 10, 0), Background = fill,
                BorderBrush = current ? new SolidColorBrush(Theme.Accent) : Theme.FgAlpha(0x40), BorderThickness = new Thickness(current ? 2 : 1)
            };
            if (c == Colors.Transparent) // "off": an empty ring with a power symbol
                dot.Child = new TextBlock { Text = "", FontFamily = Glyphs.IconFont, FontSize = 12, Foreground = Theme.FgAlpha(0xB0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return dot;
        }
    }
}
