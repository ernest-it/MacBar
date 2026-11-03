using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MacBar
{
    public class AppWindow
    {
        public IntPtr Hwnd; public uint Pid; public string ExePath; public string Aumid; public string Title; public bool IsUwp;
    }

    public static class WindowTracker
    {
        static readonly uint myPid = (uint)Process.GetCurrentProcess().Id;
        static readonly Dictionary<uint, string> pathCache = new Dictionary<uint, string>();

        public static List<AppWindow> GetAppWindows()
        {
            var list = new List<AppWindow>();
            Native.EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                var w = Inspect(h);
                if (w != null) list.Add(w);
                return true;
            }, IntPtr.Zero);
            if (pathCache.Count > 400) pathCache.Clear();
            return list;
        }

        public static string ProcPath(uint pid)
        {
            string p;
            if (pathCache.TryGetValue(pid, out p)) return p;
            p = Native.GetProcessPath(pid);
            if (p != null) pathCache[pid] = p;
            return p;
        }

        public static bool IsTaskbarWindow(IntPtr h)
        {
            if (!Native.IsWindowVisible(h)) return false;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
            if (!appWindow)
            {
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return false;
                if ((ex & Native.WS_EX_NOACTIVATE) != 0) return false;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false;
            }
            int cloaked;
            if (Native.DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return false;
            if (Native.GetWindowTextLength(h) == 0) return false;
            string cls = Native.GetClass(h);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
            return true;
        }

        static AppWindow Inspect(IntPtr h)
        {
            if (!IsTaskbarWindow(h)) return null;
            uint pid = Native.GetPid(h);
            if (pid == myPid) return null;
            var w = new AppWindow { Hwnd = h, Pid = pid, Title = Native.GetWindowTitle(h) };
            w.ExePath = ProcPath(pid);
            w.Aumid = Native.GetAumid(h);
            if (w.ExePath != null && w.ExePath.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                w.IsUwp = true;
                IntPtr core = Native.FindWindowEx(h, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                if (core != IntPtr.Zero)
                {
                    string real = ProcPath(Native.GetPid(core));
                    if (real != null) w.ExePath = real;
                }
            }
            if (w.ExePath != null && w.ExePath.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0) w.IsUwp = true;
            return w;
        }

        public static void Activate(IntPtr h)
        {
            if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
            Native.SetForegroundWindow(h);
            if (Native.GetForegroundWindow() != h)
            {
                // Tapping Alt lets us take foreground when Windows' focus lock refuses.
                Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
                Native.SetForegroundWindow(h);
                Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
            }
        }
    }

    public static class AppNames
    {
        static readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string ForExe(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return null;
            string n;
            if (cache.TryGetValue(exe, out n)) return n;
            string file = Path.GetFileName(exe).ToLowerInvariant();
            if (file == "explorer.exe") n = "File Explorer";
            else
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(exe);
                    n = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription.Trim()
                      : !string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName.Trim() : null;
                }
                catch { }
                if (string.IsNullOrEmpty(n)) n = Path.GetFileNameWithoutExtension(exe);
            }
            cache[exe] = n;
            return n;
        }

        public static string ForWindow(IntPtr h)
        {
            string cls = Native.GetClass(h);
            if (cls == "Progman" || cls == "WorkerW") return "Desktop";
            string exe = WindowTracker.ProcPath(Native.GetPid(h));
            if (exe == null) return Native.GetWindowTitle(h);
            if (exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)) return Native.GetWindowTitle(h);
            return ForExe(exe);
        }
    }

    public static class ShellUtil
    {
        public static void Open(string target)
        {
            try
            {
                if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                    Process.Start("explorer.exe", target);
                else
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("Couldn't open " + target + "\n\n" + ex.Message, "MacBar"); }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint n);

        static Dictionary<string, string> shortcutByTarget, shortcutByDir;

        /// <summary>
        /// Some apps' windows belong to a helper binary with no icon of its own (NoMachine runs as
        /// nxplayer.bin). For those, find the app's Start menu shortcut: an exact target match, or a
        /// shortcut pointing into the same folder. Returns null if the exe has its own icon.
        /// </summary>
        public static string ShortcutForIconless(string exe)
        {
            if (string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe)) return null;
            // The shell only shows a program's own icon for .exe files; anything else (.bin) gets a blank page.
            bool isExe = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            try { if (isExe && ExtractIconEx(exe, -1, null, null, 0) > 0) return null; } catch { return null; }
            if (shortcutByTarget == null)
            {
                shortcutByTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                shortcutByDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) })
                {
                    string[] lnks;
                    try { lnks = System.IO.Directory.GetFiles(root, "*.lnk", System.IO.SearchOption.AllDirectories); } catch { continue; }
                    foreach (var lnk in lnks)
                    {
                        string target = ResolveLnk(lnk);
                        if (target == null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                        string n = System.IO.Path.GetFileNameWithoutExtension(lnk).ToLowerInvariant();
                        if (n.Contains("uninstall") || n.Contains("readme")) continue;
                        if (!shortcutByTarget.ContainsKey(target)) shortcutByTarget[target] = lnk;
                        string dir = System.IO.Path.GetDirectoryName(target);
                        if (dir != null && !shortcutByDir.ContainsKey(dir)) shortcutByDir[dir] = lnk;
                    }
                }
            }
            string found;
            if (shortcutByTarget.TryGetValue(exe, out found)) return found;
            if (shortcutByDir.TryGetValue(System.IO.Path.GetDirectoryName(exe), out found)) return found;
            return null;
        }

        public static string ResolveLnk(string lnk)
        {
            object sh = null, sc = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                sh = Activator.CreateInstance(t);
                sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                var target = sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
                return string.IsNullOrEmpty(target) ? null : target;
            }
            catch { return null; }
            finally
            {
                if (sc != null) Marshal.ReleaseComObject(sc);
                if (sh != null) Marshal.ReleaseComObject(sh);
            }
        }

        public static PinnedApp PinFromFile(string path)
        {
            var p = new PinnedApp { Target = path, Name = Path.GetFileNameWithoutExtension(path) };
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                p.ExePath = ResolveLnk(path);
                p.Aumid = Native.GetAumidForFile(path);
            }
            else if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                p.ExePath = path;
                p.Name = AppNames.ForExe(path);
            }
            return p;
        }

        public static List<PinnedApp> ImportTaskbarPins()
        {
            var list = new List<PinnedApp>();
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
                if (Directory.Exists(dir))
                {
                    var files = Directory.GetFiles(dir, "*.lnk");
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    foreach (var f in files) list.Add(PinFromFile(f));
                }
            }
            catch { }
            // Make sure File Explorer is always there, like Finder.
            // Windows 11's own Explorer pin has no target path, only the AUMID "Microsoft.Windows.Explorer".
            bool hasExplorer = list.Exists(p =>
                (p.ExePath != null && p.ExePath.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(p.Aumid, "Microsoft.Windows.Explorer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(p.Name, "File Explorer", StringComparison.OrdinalIgnoreCase));
            if (!hasExplorer)
            {
                string exp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                list.Insert(0, new PinnedApp { Target = exp, ExePath = exp, Name = "File Explorer" });
            }
            return list;
        }
    }

    public static class IconLoader
    {
        static readonly Dictionary<string, ImageSource> cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        static readonly Guid IID_Factory = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");

        static readonly object gate = new object(); // icons are also loaded on the app page's background thread

        static bool TryCached(string key, out ImageSource img) { lock (gate) return cache.TryGetValue(key, out img); }
        static void Store(string key, ImageSource img) { lock (gate) cache[key] = img; }

        public static ImageSource Load(string parsingName, int px) { return Load(parsingName, px, true); }

        static ImageSource Load(string parsingName, int px, bool keep)
        {
            if (string.IsNullOrEmpty(parsingName)) return null;
            string key = parsingName + "|" + px;
            ImageSource img;
            if (TryCached(key, out img)) return img;
            img = null;
            try
            {
                IShellItemImageFactory f; Guid iid = IID_Factory;
                if (Native.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out f) == 0 && f != null)
                {
                    try
                    {
                        IntPtr hbmp; var sz = new SIZE { cx = px, cy = px };
                        if (f.GetImage(sz, 0x4 /* SIIGBF_ICONONLY */, out hbmp) == 0 && hbmp != IntPtr.Zero)
                        {
                            img = FromHBitmap(hbmp);
                            Native.DeleteObject(hbmp);
                        }
                    }
                    finally { Marshal.ReleaseComObject(f); }
                }
            }
            catch { }
            if (img != null && keep) Store(key, img);
            return img;
        }

        public static bool IsCached(string parsingName, int px) { ImageSource i; return TryCached(parsingName + "|sharp|" + px, out i); }

        /// <summary>
        /// Crisp icon at an exact pixel size: takes the largest (256 px) shell icon and downsamples it once
        /// with GDI+ high-quality bicubic, so WPF displays it 1:1 instead of scaling twice.
        /// </summary>
        public static ImageSource LoadSharp(string parsingName, int px)
        {
            if (string.IsNullOrEmpty(parsingName) || px <= 0) return null;
            string key = parsingName + "|sharp|" + px;
            ImageSource img;
            if (TryCached(key, out img)) return img;
            var big = Load(parsingName, 256, false) as BitmapSource; // only the resized copy is cached
            if (big == null) return null;
            // Apps that only ship small icons get drawn by Windows on a dark square "plate" at 256 px.
            // If the 48 px version is a normal icon, prefer it.
            int kind = Padding(big);
            if (kind != 0)
            {
                var small = Load(parsingName, 48, false) as BitmapSource;
                // A framed 256 px image always means the small icon is the real one; for a solid plate,
                // only switch if the small icon isn't the same plate (some real icons are solid squares).
                if (small != null && (kind == 2 || Padding(small) == 0)) big = small;
            }
            try { img = Resample(big, px); } catch { img = big; }
            Store(key, img);
            return img;
        }

        static BitmapSource Resample(BitmapSource src, int px)
        {
            if (src.Format != PixelFormats.Pbgra32) src = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
            int w = src.PixelWidth, h = src.PixelHeight;
            var bits = new byte[w * h * 4];
            src.CopyPixels(bits, w * 4, 0);

            // Old apps without a big icon come back as a small icon in the corner of a 256 canvas; crop to it.
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (bits[(y * w + x) * 4 + 3] > 8) { if (x < minX) minX = x; if (y < minY) minY = y; if (x > maxX) maxX = x; if (y > maxY) maxY = y; }
            var srcRect = new System.Drawing.Rectangle(0, 0, w, h);
            if (maxX >= 0)
            {
                int cw = maxX - minX + 1, ch = maxY - minY + 1;
                if (Math.Max(cw, ch) <= w / 3)
                {
                    int side = Math.Max(cw, ch) <= 16 ? 16 : Math.Max(cw, ch) <= 32 ? 32 : 48;
                    srcRect = new System.Drawing.Rectangle(minX, minY, Math.Min(side, w - minX), Math.Min(side, h - minY));
                }
            }

            using (var sb = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            using (var db = new System.Drawing.Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                var sd = sb.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, sb.PixelFormat);
                for (int y = 0; y < h; y++) Marshal.Copy(bits, y * w * 4, sd.Scan0 + y * sd.Stride, w * 4);
                sb.UnlockBits(sd);
                using (var g = System.Drawing.Graphics.FromImage(db))
                using (var ia = new System.Drawing.Imaging.ImageAttributes())
                {
                    g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    ia.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                    g.DrawImage(sb, new System.Drawing.Rectangle(0, 0, px, px), srcRect.X, srcRect.Y, srcRect.Width, srcRect.Height, System.Drawing.GraphicsUnit.Pixel, ia);
                }
                var dd = db.LockBits(new System.Drawing.Rectangle(0, 0, px, px), System.Drawing.Imaging.ImageLockMode.ReadOnly, db.PixelFormat);
                var outBits = new byte[px * px * 4];
                for (int y = 0; y < px; y++) Marshal.Copy(dd.Scan0 + y * dd.Stride, outBits, y * px * 4, px * 4);
                db.UnlockBits(dd);
                // Downscaling a big icon a lot softens it; a light unsharp mask restores crisp edges.
                double factor = (double)Math.Max(srcRect.Width, srcRect.Height) / px;
                if (factor > 1.05) Sharpen(outBits, px, factor >= 2 ? 0.45 : 0.3);
                var result = BitmapSource.Create(px, px, 96, 96, PixelFormats.Pbgra32, null, outBits, px * 4);
                result.Freeze();
                return result;
            }
        }

        /// <summary>0 = normal icon, 1 = opaque near-uniform square plate behind it, 2 = faint outline frame with the icon tiny in the middle.</summary>
        static int Padding(BitmapSource src)
        {
            if (src.Format != PixelFormats.Pbgra32) src = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);
            int w = src.PixelWidth, h = src.PixelHeight;
            if (w < 16 || h < 16) return 0;
            var px = new byte[w * h * 4];
            src.CopyPixels(px, w * 4, 0);
            int inset0 = Math.Max(1, (int)(w * 0.06)), inset1 = Math.Max(inset0 + 1, (int)(w * 0.12));
            int n = 0, opaque = 0; double sum = 0, sumSq = 0;
            for (int y = inset0; y < h - inset0; y += 2)
                for (int x = inset0; x < w - inset0; x += 2)
                {
                    bool band = x < inset1 || y < inset1 || x >= w - inset1 || y >= h - inset1;
                    if (!band) continue;
                    int i = (y * w + x) * 4;
                    n++;
                    if (px[i + 3] < 230) continue;
                    opaque++;
                    double lum = 0.11 * px[i] + 0.59 * px[i + 1] + 0.3 * px[i + 2];
                    sum += lum; sumSq += lum * lum;
                }
            if (n > 0 && opaque >= n * 0.9)
            {
                double mean = sum / opaque, sd = Math.Sqrt(Math.Max(0, sumSq / opaque - mean * mean));
                if (sd < 10) return 1;
            }
            return LooksFramed(px, w, h) ? 2 : 0;
        }

        /// <summary>
        /// The other way Windows pads small icons: a faint (semi-transparent) outline around the edge,
        /// an empty interior, and the real icon tiny in the middle.
        /// </summary>
        static bool LooksFramed(byte[] px, int w, int h)
        {
            int edge = 0, faint = 0, inner = 0, empty = 0;
            int i0 = (int)(w * 0.10), i1 = (int)(w * 0.25);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int a = px[(y * w + x) * 4 + 3];
                    int d = Math.Min(Math.Min(x, y), Math.Min(w - 1 - x, h - 1 - y));
                    if (d <= 3) { edge++; if (a >= 20 && a <= 160) faint++; }
                    else if (d >= i0 && d < i1) { inner++; if (a < 8) empty++; }
                }
            return edge > 0 && inner > 0 && faint > edge * 0.5 && empty > inner * 0.95;
        }

        /// <summary>Unsharp mask (3x3) on premultiplied BGRA; alpha is left unchanged.</summary>
        static void Sharpen(byte[] p, int n, double amount)
        {
            var src = (byte[])p.Clone();
            for (int y = 1; y < n - 1; y++)
                for (int x = 1; x < n - 1; x++)
                {
                    int i = (y * n + x) * 4;
                    int a = src[i + 3];
                    if (a == 0) continue;
                    for (int c = 0; c < 3; c++)
                    {
                        int sum = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                                sum += src[((y + dy) * n + (x + dx)) * 4 + c];
                        double blur = sum / 9.0;
                        double v = src[i + c] + amount * (src[i + c] - blur);
                        p[i + c] = (byte)Math.Max(0, Math.Min(a, Math.Round(v))); // premultiplied: color <= alpha
                    }
                }
        }

        public static ImageSource ForWindow(IntPtr h)
        {
            IntPtr icon;
            Native.SendMessageTimeout(h, Native.WM_GETICON, new IntPtr(1), IntPtr.Zero, 2, 100, out icon);
            if (icon == IntPtr.Zero) Native.SendMessageTimeout(h, Native.WM_GETICON, new IntPtr(2), IntPtr.Zero, 2, 100, out icon);
            if (icon == IntPtr.Zero) icon = Native.GetClassLongPtr(h, -14);
            if (icon == IntPtr.Zero) icon = Native.GetClassLongPtr(h, -34);
            return FromHIcon(icon);
        }

        public static BitmapSource FromHIcon(IntPtr hicon)
        {
            if (hicon == IntPtr.Zero) return null;
            try
            {
                var bs = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                return bs;
            }
            catch { return null; }
        }

        static BitmapSource FromHBitmap(IntPtr hbmp)
        {
            BITMAP bm;
            if (Native.GetObject(hbmp, Marshal.SizeOf(typeof(BITMAP)), out bm) == 0) return null;
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            var bih = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var bits = new byte[w * h * 4];
            IntPtr dc = Native.GetDC(IntPtr.Zero);
            try { Native.GetDIBits(dc, hbmp, 0, (uint)h, bits, ref bih, 0); }
            finally { Native.ReleaseDC(IntPtr.Zero, dc); }
            bool anyAlpha = false;
            for (int i = 3; i < bits.Length; i += 4) if (bits[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha)
            {
                // Legacy icons without an alpha channel come back on a black background: treat pure black
                // as transparent so the icon can be cropped and shown without a dark square around it.
                for (int i = 0; i < bits.Length; i += 4)
                    bits[i + 3] = (byte)(bits[i] == 0 && bits[i + 1] == 0 && bits[i + 2] == 0 ? 0 : 255);
            }
            var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, bits, w * 4);
            bs.Freeze();
            return bs;
        }
    }
}
