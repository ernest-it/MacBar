using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Threading;

namespace MacBar
{
    public class WsApp
    {
        public string Name { get; set; }
        /// <summary>Identifies the app's windows: exe file name, plus "|profile:X" or "|app:X" for Chrome profiles and web apps.</summary>
        public string Key { get; set; }
        public string LaunchPath { get; set; }
        public string LaunchArgs { get; set; }
        public string TitleHint { get; set; }
        public bool Launch { get; set; }          // open it when no window is found
        public string State { get; set; }         // Normal | Maximized | Minimized
        public string Monitor { get; set; }       // device name at capture (\\.\DISPLAY1)
        public int MonL { get; set; } public int MonT { get; set; } public int MonW { get; set; } public int MonH { get; set; }
        public int X { get; set; } public int Y { get; set; } public int W { get; set; } public int H { get; set; } // visible frame, relative to the monitor

        public WsApp() { Launch = true; State = "Normal"; LaunchArgs = ""; }

        public string ChromeProfile
        {
            get { var m = Regex.Match(Key ?? "", @"\|profile:(.+)$"); return m.Success ? m.Groups[1].Value : null; }
        }
        public bool IsChrome { get { return (Key ?? "").StartsWith("chrome.exe", StringComparison.OrdinalIgnoreCase) && (Key ?? "").IndexOf("|app:", StringComparison.Ordinal) < 0; } }
    }

    public class Workspace
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool RunAtSignIn { get; set; }
        public bool SignInOpensApps { get; set; } // false: only arrange what's already open
        public List<WsApp> Apps { get; set; }
        public Workspace() { Id = Guid.NewGuid().ToString("N"); Apps = new List<WsApp>(); SignInOpensApps = true; }
    }

    /// <summary>
    /// MacBar's own take on PowerToys Workspaces: saves where each app's windows go, reuses windows that are
    /// already open (matched by program and Chrome profile) instead of starting new copies, opens only what's
    /// missing, aligns windows by their visible edges, and keeps re-checking for a few seconds so apps that
    /// move themselves after launch end up where they belong.
    /// </summary>
    public static class Workspaces
    {
        public static List<Workspace> All = new List<Workspace>();
        public static event Action Changed;
        static string FilePath { get { return Path.Combine(Settings.Dir, "workspaces.json"); } }

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var l = new JavaScriptSerializer().Deserialize<List<Workspace>>(File.ReadAllText(FilePath));
                    if (l != null) All = l.Where(w => w != null).ToList();
                    foreach (var w in All) if (w.Apps == null) w.Apps = new List<WsApp>();
                }
            }
            catch (Exception ex) { App.Trace("workspaces load failed: " + ex.Message); }
        }

        public static void Save()
        {
            if (Demo.On) return;
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(All));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
            }
            catch (Exception ex) { App.Trace("workspaces save failed: " + ex.Message); }
            if (Changed != null) Changed();
        }

        // ---------------------------------------------------------------- native

        [StructLayout(LayoutKind.Sequential)] struct R { public int L, T, Rt, B; public int W { get { return Rt - L; } } public int H { get { return B - T; } } }
        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct WINDOWPLACEMENT { public int length, flags, showCmd; public PT min, max; public R normal; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct MONITORINFOEX { public int cbSize; public R rcMonitor, rcWork; public int dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
        delegate bool MonitorEnumProc(IntPtr h, IntPtr hdc, ref R r, IntPtr data);

        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFOEX mi);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out R r);
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out R r, int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);

        /// <summary>The Store app ID a running packaged process belongs to (e.g. Spotify's), or null.</summary>
        static string ProcessAumid(uint pid)
        {
            IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                uint len = 256; var sb = new StringBuilder((int)len);
                return GetApplicationUserModelId(h, ref len, sb) == 0 && sb.Length > 0 ? sb.ToString() : null;
            }
            catch { return null; }
            finally { CloseHandle(h); }
        }

        static readonly IntPtr PerMonitorV2 = new IntPtr(-4);
        const uint SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
        const int SW_MAXIMIZE = 3, SW_SHOWNOACTIVATE = 4, SW_SHOWMINNOACTIVE = 7, SW_RESTORE = 9;

        /// <summary>Runs code with physical-pixel coordinates (every window and monitor at its real size).</summary>
        static T Physical<T>(Func<T> f)
        {
            IntPtr old = IntPtr.Zero;
            try { old = SetThreadDpiAwarenessContext(PerMonitorV2); } catch { }
            try { return f(); }
            finally { if (old != IntPtr.Zero) try { SetThreadDpiAwarenessContext(old); } catch { } }
        }

        // ---------------------------------------------------------------- monitors

        class Mon { public string Device; public R Rect, Work; public bool Primary; }

        static List<Mon> Monitors()
        {
            var list = new List<Mon>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate (IntPtr h, IntPtr hdc, ref R r, IntPtr d)
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                if (GetMonitorInfo(h, ref mi)) list.Add(new Mon { Device = mi.szDevice, Rect = mi.rcMonitor, Work = mi.rcWork, Primary = (mi.dwFlags & 1) != 0 });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        static Mon MonitorAt(List<Mon> mons, int x, int y)
        {
            foreach (var m in mons) if (x >= m.Rect.L && x < m.Rect.Rt && y >= m.Rect.T && y < m.Rect.B) return m;
            return mons.OrderBy(m => Math.Abs(m.Rect.L + m.Rect.W / 2 - x) + Math.Abs(m.Rect.T + m.Rect.H / 2 - y)).FirstOrDefault();
        }

        /// <summary>Same monitor as at capture: same position and size, else same name, else same size, else the primary.</summary>
        static Mon Resolve(WsApp a, List<Mon> mons)
        {
            return mons.FirstOrDefault(m => m.Rect.L == a.MonL && m.Rect.T == a.MonT && m.Rect.W == a.MonW && m.Rect.H == a.MonH)
                ?? mons.FirstOrDefault(m => m.Device == a.Monitor)
                ?? mons.Where(m => m.Rect.W == a.MonW && m.Rect.H == a.MonH).OrderBy(m => Math.Abs(m.Rect.L - a.MonL) + Math.Abs(m.Rect.T - a.MonT)).FirstOrDefault()
                ?? mons.FirstOrDefault(m => m.Primary) ?? mons.First();
        }

        // ---------------------------------------------------------------- windows

        class Win
        {
            public IntPtr H; public uint Pid; public string Exe, Key, Title, Relaunch, Aumid; public bool Min, Max, Uwp; public R Frame, Rect;
        }

        static readonly int myPid = Process.GetCurrentProcess().Id;

        /// <summary>Top-level app windows (same rules as the dock), including ones on other virtual desktops.</summary>
        static List<Win> Snapshot()
        {
            var list = new List<Win>();
            Native.EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                try { var w = Inspect(h); if (w != null) list.Add(w); } catch { }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        static Win Inspect(IntPtr h)
        {
            if (!Native.IsWindowVisible(h)) return null;
            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            if ((ex & Native.WS_EX_APPWINDOW) == 0)
            {
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0 || (ex & Native.WS_EX_NOACTIVATE) != 0) return null;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return null;
            }
            int cloaked;
            // 2 = cloaked by the shell (on another virtual desktop): still counts as open. Anything else is hidden.
            if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0 && cloaked != 2) return null;
            if (Native.GetWindowTextLength(h) == 0) return null;
            string cls = Native.GetClass(h);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" || cls == "Windows.UI.Core.CoreWindow") return null;
            uint pid = Native.GetPid(h);
            if (pid == myPid) return null;

            var w = new Win { H = h, Pid = pid, Title = Native.GetWindowTitle(h), Exe = Native.GetProcessPath(pid) ?? "", Min = IsIconic(h), Max = IsZoomed(h) };
            string file = Path.GetFileName(w.Exe).ToLowerInvariant();
            if (file == "textinputhost.exe" || file == "shellexperiencehost.exe" || file == "startmenuexperiencehost.exe" || file == "searchhost.exe" || file == "lockapp.exe") return null;
            w.Aumid = Native.GetAumid(h);
            if (file == "applicationframehost.exe")
            {
                // UWP app inside a frame: the frame carries the app's ID (its CoreWindow is gone while minimized).
                w.Uwp = true;
                IntPtr core = Native.FindWindowEx(h, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
                if (core != IntPtr.Zero) w.Exe = Native.GetProcessPath(Native.GetPid(core)) ?? w.Exe;
                w.Key = !string.IsNullOrEmpty(w.Aumid) ? "uwp:" + w.Aumid.ToLowerInvariant() : Path.GetFileName(w.Exe).ToLowerInvariant();
            }
            else
            {
                w.Relaunch = WindowProp(h, 2); // System.AppUserModel.RelaunchCommand (Chrome puts --profile-directory here)
                w.Key = KeyFor(w.Exe, w.Relaunch, w.Aumid);
            }
            GetWindowRect(h, out w.Rect);
            if (DwmGetWindowAttribute(h, 9 /* EXTENDED_FRAME_BOUNDS */, out w.Frame, 16) != 0) w.Frame = w.Rect;
            return w;
        }

        static string WindowProp(IntPtr h, int pid)
        {
            try
            {
                IPropertyStore ps; Guid iid = typeof(IPropertyStore).GUID;
                if (Native.SHGetPropertyStoreForWindow(h, ref iid, out ps) != 0 || ps == null) return null;
                try { return Native.ReadString(ps, new PROPERTYKEY(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid)); }
                finally { Marshal.ReleaseComObject(ps); }
            }
            catch { return null; }
        }

        static void SplitCommand(string cmd, out string exe, out string args)
        {
            cmd = (cmd ?? "").Trim();
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                exe = end > 0 ? cmd.Substring(1, end - 1) : cmd.Trim('"');
                args = end > 0 ? cmd.Substring(end + 1).Trim() : "";
            }
            else
            {
                // Unquoted paths may contain spaces ("C:\Program Files (x86)\Steam\steam.exe"): split after ".exe".
                int e = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                int sp = e > 0 ? e + 4 : cmd.IndexOf(' ');
                if (sp <= 0 || sp > cmd.Length) sp = cmd.Length;
                exe = cmd.Substring(0, sp);
                args = cmd.Substring(sp).Trim();
            }
        }

        static string Arg(string args, string name)
        {
            var m = Regex.Match(args ?? "", "--" + name + "=(\"([^\"]*)\"|(\\S+))");
            return m.Success ? (m.Groups[2].Success && m.Groups[2].Length > 0 ? m.Groups[2].Value : m.Groups[3].Value) : null;
        }

        static string KeyFor(string exePath, string relaunch, string aumid)
        {
            string exe = Path.GetFileName(exePath ?? "").ToLowerInvariant();
            string suffix = "";
            if (!string.IsNullOrEmpty(relaunch))
            {
                string rexe, rargs;
                SplitCommand(relaunch, out rexe, out rargs);
                string rname = Path.GetFileName(rexe).ToLowerInvariant();
                if (rname.EndsWith(".exe")) exe = rname; // e.g. Steam's windows belong to steamwebhelper.exe but relaunch steam.exe
                string app = Arg(rargs, "app-id"), profile = Arg(rargs, "profile-directory");
                if (app != null) suffix = "|app:" + app;
                else if (profile != null) suffix = "|profile:" + profile;
            }
            return exe + suffix;
        }

        /// <summary>A saved app with no profile/app suffix matches any window of that program.</summary>
        static bool Matches(string appKey, string winKey)
        {
            if (string.Equals(appKey, winKey, StringComparison.OrdinalIgnoreCase)) return true;
            return appKey.IndexOf('|') < 0 && winKey.StartsWith(appKey + "|", StringComparison.OrdinalIgnoreCase)
                   && winKey.IndexOf("|app:", StringComparison.Ordinal) < 0; // a PWA isn't "Chrome"
        }

        // ---------------------------------------------------------------- capture

        public static Dictionary<string, string> ChromeProfiles()
        {
            var d = new Dictionary<string, string>();
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\User Data\Local State");
                var root = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                var cache = (Dictionary<string, object>)((Dictionary<string, object>)root["profile"])["info_cache"];
                foreach (var kv in cache)
                {
                    var p = kv.Value as Dictionary<string, object>;
                    string name = p != null && p.ContainsKey("name") ? p["name"] as string : kv.Key;
                    string user = p != null && p.ContainsKey("user_name") ? p["user_name"] as string : null;
                    d[kv.Key] = string.IsNullOrEmpty(user) ? name : name + " (" + user + ")";
                }
            }
            catch { }
            return d;
        }

        static string ChromeExe()
        {
            foreach (var p in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
            {
                string f = Path.Combine(p, @"Google\Chrome\Application\chrome.exe");
                if (File.Exists(f)) return f;
            }
            return "chrome.exe";
        }

        /// <summary>Sets an app's Chrome profile (key, launch command and name).</summary>
        public static void SetChromeProfile(WsApp a, string profileDir)
        {
            a.Key = "chrome.exe" + (string.IsNullOrEmpty(profileDir) ? "" : "|profile:" + profileDir);
            if (string.IsNullOrEmpty(a.LaunchPath) || !a.LaunchPath.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase)) a.LaunchPath = ChromeExe();
            a.LaunchArgs = string.IsNullOrEmpty(profileDir) ? "" : "--profile-directory=\"" + profileDir + "\"";
            string label;
            a.Name = string.IsNullOrEmpty(profileDir) ? "Google Chrome" : "Google Chrome: " + (ChromeProfiles().TryGetValue(profileDir, out label) ? label.Split(' ')[0] : profileDir);
        }

        /// <summary>Package AUMID for a Store app's exe (PackageFamilyName!AppId from its AppxManifest.xml).</summary>
        static string PackagedAumid(string exe)
        {
            try
            {
                int i = exe.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);
                if (i < 0) return null;
                string rest = exe.Substring(i + 13);
                string full = rest.Substring(0, rest.IndexOf('\\'));
                string dir = exe.Substring(0, i + 13) + full;
                var parts = full.Split('_');
                string family = parts[0] + "_" + parts[parts.Length - 1];
                string xml = File.ReadAllText(Path.Combine(dir, "AppxManifest.xml"));
                string rel = exe.Substring(dir.Length + 1);
                foreach (Match m in Regex.Matches(xml, "<Application\\b[^>]*>", RegexOptions.IgnoreCase))
                {
                    string id = Regex.Match(m.Value, "Id=\"([^\"]+)\"").Groups[1].Value;
                    string ex = Regex.Match(m.Value, "Executable=\"([^\"]+)\"").Groups[1].Value;
                    if (id.Length > 0 && string.Equals(ex.Replace('/', '\\'), rel, StringComparison.OrdinalIgnoreCase)) return family + "!" + id;
                }
                // The window's exe isn't a declared entry point (Spotify.exe is started by SpotifyMigrator.exe):
                // use the package's first app.
                var first = Regex.Match(xml, "<Application\\b[^>]*\\bId=\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (first.Success) return family + "!" + first.Groups[1].Value;
            }
            catch { }
            return null;
        }

        static WsApp Describe(Win w, List<Mon> mons, Dictionary<string, string> profiles)
        {
            var a = new WsApp { Key = w.Key, TitleHint = w.Title, State = w.Min ? "Minimized" : w.Max ? "Maximized" : "Normal" };
            // Launch command: the window's own relaunch command, else the Store app, else the exe (or its shortcut).
            string nameExe = w.Exe;
            if (w.Uwp && !string.IsNullOrEmpty(w.Aumid))
                a.LaunchPath = "shell:AppsFolder\\" + w.Aumid;
            else if (!string.IsNullOrEmpty(w.Relaunch))
            {
                string exe, args; SplitCommand(w.Relaunch, out exe, out args);
                a.LaunchPath = exe; a.LaunchArgs = args;
                if (File.Exists(exe)) nameExe = exe; // Steam's windows: name it after steam.exe, not its web helper
            }
            else if (w.Exe.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string aumid = ProcessAumid(w.Pid) ?? (!string.IsNullOrEmpty(w.Aumid) ? w.Aumid : PackagedAumid(w.Exe));
                a.LaunchPath = aumid != null ? "shell:AppsFolder\\" + aumid : w.Exe;
            }
            else
            {
                // Squirrel apps (Discord, Teams classic, ...) live in a versioned app-x.y.z folder that changes on
                // every update; their stable launcher is Update.exe --processStart.
                var sq = Regex.Match(w.Exe, @"^(.*)\\app-[\d.]+\\([^\\]+\.exe)$", RegexOptions.IgnoreCase);
                if (sq.Success && File.Exists(Path.Combine(sq.Groups[1].Value, "Update.exe")))
                {
                    a.LaunchPath = Path.Combine(sq.Groups[1].Value, "Update.exe");
                    a.LaunchArgs = "--processStart \"" + sq.Groups[2].Value + "\"";
                }
                else a.LaunchPath = ShellUtil.ShortcutForIconless(w.Exe) ?? w.Exe;
            }

            string profile = a.ChromeProfile, label;
            if (a.IsChrome && profile != null) a.Name = "Google Chrome: " + (profiles.TryGetValue(profile, out label) ? label.Split(' ')[0] : profile);
            else if (a.Key.Contains("|app:") || w.Uwp) a.Name = w.Title;
            else a.Name = AppNames.ForExe(nameExe) ?? w.Title;
            if (a.Key.StartsWith("explorer.exe")) a.Name = "File Explorer";

            // Where it sits: the visible frame. Minimized/maximized windows use their restored position.
            R frame = w.Frame;
            if (w.Min || w.Max)
            {
                var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
                GetWindowPlacement(w.H, ref wp);
                R n = wp.normal;
                // rcNormalPosition is in "workspace" coordinates (relative to the work area) unless it's a tool window.
                var m0 = MonitorAt(mons, (n.L + n.Rt) / 2, (n.T + n.B) / 2);
                if (m0 != null) { int dx = m0.Work.L - m0.Rect.L, dy = m0.Work.T - m0.Rect.T; n.L += dx; n.Rt += dx; n.T += dy; n.B += dy; }
                bool bordered = (GetWindowLong(w.H, -16) & 0x00C40000) == 0x00C40000; // caption + sizing frame: 7 px invisible borders
                int b = bordered ? 7 : 0;
                frame = new R { L = n.L + b, T = n.T, Rt = n.Rt - b, B = n.B - b };
                if (w.Max) { var mm = MonitorAt(mons, (w.Frame.L + w.Frame.Rt) / 2, (w.Frame.T + w.Frame.B) / 2); if (mm != null && MonitorAt(mons, (frame.L + frame.Rt) / 2, (frame.T + frame.B) / 2) != mm) frame = Recenter(frame, mm); }
            }
            var mon = MonitorAt(mons, (frame.L + frame.Rt) / 2, (frame.T + frame.B) / 2);
            if (w.Max) mon = MonitorAt(mons, (w.Frame.L + w.Frame.Rt) / 2, (w.Frame.T + w.Frame.B) / 2) ?? mon;
            a.Monitor = mon.Device; a.MonL = mon.Rect.L; a.MonT = mon.Rect.T; a.MonW = mon.Rect.W; a.MonH = mon.Rect.H;
            a.X = frame.L - mon.Rect.L; a.Y = frame.T - mon.Rect.T; a.W = Math.Max(100, frame.W); a.H = Math.Max(60, frame.H);
            return a;
        }

        static R Recenter(R r, Mon m)
        {
            int w = Math.Min(r.W, m.Work.W), h = Math.Min(r.H, m.Work.H);
            int x = m.Work.L + (m.Work.W - w) / 2, y = m.Work.T + (m.Work.H - h) / 2;
            return new R { L = x, T = y, Rt = x + w, B = y + h };
        }

        /// <summary>Every open app window, as workspace entries (left to right, top to bottom).</summary>
        public static List<WsApp> CaptureOpenWindows()
        {
            return Physical(delegate
            {
                var mons = Monitors();
                var profiles = ChromeProfiles();
                return Snapshot().Where(w => w.Key.Length > 0).Select(w => Describe(w, mons, profiles))
                                 .OrderBy(a => a.MonL).ThenBy(a => a.Y).ThenBy(a => a.X).ToList();
            });
        }

        /// <summary>
        /// Replaces the workspace's apps with the windows open now, keeping per-app choices (open if missing)
        /// for apps that were already in it.
        /// </summary>
        public static void UpdateFromOpenWindows(Workspace ws)
        {
            var fresh = CaptureOpenWindows();
            var old = ws.Apps.ToList();
            foreach (var a in fresh)
            {
                var prev = old.FirstOrDefault(o => string.Equals(o.Key, a.Key, StringComparison.OrdinalIgnoreCase));
                if (prev != null) { a.Launch = prev.Launch; old.Remove(prev); }
            }
            ws.Apps = fresh;
        }

        // ---------------------------------------------------------------- monitor list for the editor preview

        public class MonitorInfo { public string Device; public int L, T, W, H; public bool Primary; }
        public static List<MonitorInfo> CurrentMonitors()
        {
            return Physical(delegate { return Monitors().Select(m => new MonitorInfo { Device = m.Device, L = m.Rect.L, T = m.Rect.T, W = m.Rect.W, H = m.Rect.H, Primary = m.Primary }).ToList(); });
        }

        /// <summary>Where an app will be placed on the current monitors (frame rect, physical pixels).</summary>
        public static void TargetRect(WsApp a, out int x, out int y, out int w, out int h)
        {
            var m = Physical(delegate { return Resolve(a, Monitors()); });
            R t = Target(a, m);
            x = t.L; y = t.T; w = t.W; h = t.H;
        }

        static R Target(WsApp a, Mon m)
        {
            // Scale if the monitor's resolution changed since capture.
            double sx = a.MonW > 0 ? m.Rect.W / (double)a.MonW : 1, sy = a.MonH > 0 ? m.Rect.H / (double)a.MonH : 1;
            int x = m.Rect.L + (int)Math.Round(a.X * sx), y = m.Rect.T + (int)Math.Round(a.Y * sy);
            int w = (int)Math.Round(a.W * sx), h = (int)Math.Round(a.H * sy);
            return new R { L = x, T = y, Rt = x + w, B = y + h };
        }

        // ---------------------------------------------------------------- arranging

        static int runGeneration;

        public class Result { public string Workspace; public int Arranged, Opened; public List<string> Problems = new List<string>(); }

        /// <summary>
        /// Arranges a workspace in the background. openMissing: start apps that have no window (only those marked
        /// "open if missing"). watchSeconds: how long to keep waiting for windows to appear and re-checking
        /// positions. done is called on the UI thread.
        /// </summary>
        public static void Run(Workspace ws, bool openMissing, int watchSeconds, Dispatcher ui, Action<Result> done)
        {
            int gen = Interlocked.Increment(ref runGeneration);
            var apps = ws.Apps.ToList();
            var t = new Thread(delegate ()
            {
                var res = new Result { Workspace = ws.Name };
                bool reported = false;
                Action report = delegate
                {
                    if (reported) return;
                    reported = true;
                    if (done != null && gen == runGeneration) ui.BeginInvoke(new Action(delegate { done(res); }));
                };
                try { Physical(delegate { Arrange(apps, openMissing, watchSeconds, gen, res, report); return 0; }); }
                catch (Exception ex) { res.Problems.Add("Error: " + ex.Message); App.Trace("workspace run failed: " + ex); }
                report();
            });
            t.IsBackground = true; t.Name = "Workspace";
            t.SetApartmentState(ApartmentState.STA); // shell property stores
            t.Start();
        }

        class Slot { public WsApp App; public Win Win; public DateTime PlacedAt; public int Checks; public bool Failed; }

        /// <summary>ready: called once every app has a window (or there's nothing left to wait for); re-checks continue after.</summary>
        static void Arrange(List<WsApp> apps, bool openMissing, int watchSeconds, int gen, Result res, Action ready)
        {
            var mons = Monitors();
            var before = Snapshot();
            var known = new HashSet<IntPtr>(before.Select(w => w.H));
            var claimed = new HashSet<IntPtr>();
            var slots = apps.Select(a => new Slot { App = a }).ToList();

            Assign(slots, before, claimed, mons, null);
            foreach (var s in slots.Where(s => s.Win != null)) PlaceAndRecord(s, mons, res);

            var launched = new HashSet<Slot>();
            if (openMissing)
            {
                // One launch per program+profile even if the workspace wants two of its windows.
                foreach (var g in slots.Where(s => s.Win == null && s.App.Launch).GroupBy(s => s.App.Key.ToLowerInvariant()))
                {
                    var first = g.First();
                    if (Launch(first.App, res)) { foreach (var s in g) launched.Add(s); res.Opened++; }
                }
            }

            var deadline = DateTime.Now.AddSeconds(Math.Max(watchSeconds, launched.Count > 0 ? 45 : 8));
            int[] recheckMs = { 600, 1500, 3000, 6000 };
            while (DateTime.Now < deadline && gen == runGeneration)
            {
                bool waiting = slots.Any(s => s.Win == null && (launched.Contains(s) || watchSeconds > 8));
                bool checking = slots.Any(s => s.Win != null && !s.Failed && s.Checks < recheckMs.Length);
                if (!waiting && ready != null) ready();
                if (!waiting && !checking) break;
                Thread.Sleep(250);
                var now = Snapshot();
                var alive = new HashSet<IntPtr>(now.Select(w => w.H));

                // Windows that vanished (splash screens, updaters): look again.
                foreach (var s in slots.Where(s => s.Win != null && !alive.Contains(s.Win.H)))
                { claimed.Remove(s.Win.H); if (!s.Failed) res.Arranged--; s.Win = null; s.Checks = 0; s.Failed = false; }

                if (slots.Any(s => s.Win == null))
                {
                    var newly = slots.Where(s => s.Win == null).ToList();
                    Assign(newly, now, claimed, mons, known);
                    foreach (var s in newly.Where(s => s.Win != null)) PlaceAndRecord(s, mons, res);
                }

                // Re-check placed windows a few times: apps often restore their own saved position after showing.
                foreach (var s in slots.Where(s => s.Win != null && !s.Failed && s.Checks < recheckMs.Length))
                {
                    if ((DateTime.Now - s.PlacedAt).TotalMilliseconds < recheckMs[s.Checks]) continue;
                    s.Checks++;
                    var cur = now.FirstOrDefault(w => w.H == s.Win.H);
                    if (cur != null && !InPlace(cur, s.App, mons)) { App.Trace("workspace: " + s.App.Name + " moved itself, placing again"); Place(cur, s.App, mons); }
                }
            }
            foreach (var s in slots.Where(s => s.Win == null && launched.Contains(s)))
                res.Problems.Add(s.App.Name + ": opened, but no window appeared");
            if (ready != null) ready();
        }

        static void PlaceAndRecord(Slot s, List<Mon> mons, Result res)
        {
            string err = InPlace(s.Win, s.App, mons) ? null : Place(s.Win, s.App, mons);
            s.PlacedAt = DateTime.Now; s.Checks = 0;
            if (err != null) { s.Failed = true; res.Problems.Add(s.App.Name + ": " + err); }
            else res.Arranged++;
        }

        /// <summary>
        /// Matches windows to apps. Same program (and Chrome profile) only; among several, a matching title wins,
        /// then the window closest to where it should go. preferNotIn: windows to use only as a last resort
        /// (when waiting for a launched app, prefer the windows that just appeared).
        /// </summary>
        static void Assign(List<Slot> slots, List<Win> wins, HashSet<IntPtr> claimed, List<Mon> mons, HashSet<IntPtr> preferNotIn)
        {
            var pairs = new List<Tuple<double, Slot, Win>>();
            foreach (var s in slots.Where(s => s.Win == null))
            {
                R target = Target(s.App, Resolve(s.App, mons));
                foreach (var w in wins)
                {
                    if (claimed.Contains(w.H) || !Matches(s.App.Key, w.Key)) continue;
                    // Ignore tiny windows (splash screens) unless the app itself is that small.
                    if (!w.Min && w.Frame.W < 260 && w.Frame.H < 260 && s.App.W > 400) continue;
                    double score = 0;
                    if (!string.IsNullOrEmpty(s.App.TitleHint))
                        score += w.Title == s.App.TitleHint ? 0 : (w.Title.IndexOf(s.App.TitleHint, StringComparison.OrdinalIgnoreCase) >= 0 || s.App.TitleHint.IndexOf(w.Title, StringComparison.OrdinalIgnoreCase) >= 0) ? 5000 : 10000;
                    if (!string.Equals(s.App.Key, w.Key, StringComparison.OrdinalIgnoreCase)) score += 20000;
                    if (preferNotIn != null && preferNotIn.Contains(w.H)) score += 40000;
                    score += Math.Abs((w.Frame.L + w.Frame.Rt) / 2.0 - (target.L + target.Rt) / 2.0) + Math.Abs((w.Frame.T + w.Frame.B) / 2.0 - (target.T + target.B) / 2.0);
                    pairs.Add(Tuple.Create(score, s, w));
                }
            }
            foreach (var p in pairs.OrderBy(p => p.Item1))
            {
                if (p.Item2.Win != null || claimed.Contains(p.Item3.H)) continue;
                p.Item2.Win = p.Item3; claimed.Add(p.Item3.H);
            }
        }

        static bool InPlace(Win w, WsApp a, List<Mon> mons)
        {
            var m = Resolve(a, mons);
            if (a.State == "Minimized") return w.Min;
            if (a.State == "Maximized") return w.Max && MonitorAt(mons, (w.Frame.L + w.Frame.Rt) / 2, (w.Frame.T + w.Frame.B) / 2) == m;
            if (w.Min || w.Max) return false;
            R t = Target(a, m);
            return Math.Abs(w.Frame.L - t.L) <= 3 && Math.Abs(w.Frame.T - t.T) <= 3 && Math.Abs(w.Frame.W - t.W) <= 3 && Math.Abs(w.Frame.H - t.H) <= 3;
        }

        /// <summary>Moves a window so its visible edges land exactly on the target. Returns an error or null.</summary>
        static string Place(Win w, WsApp a, List<Mon> mons)
        {
            var m = Resolve(a, mons);
            R t = Target(a, m);
            IntPtr h = w.H;
            if (IsIconic(h) || IsZoomed(h)) { ShowWindow(h, SW_SHOWNOACTIVATE); if (IsIconic(h)) ShowWindow(h, SW_RESTORE); }
            bool ok = false;
            for (int pass = 0; pass < 2; pass++) // twice: crossing to a monitor with another scale resizes the frame
            {
                R wr, fr;
                GetWindowRect(h, out wr);
                if (DwmGetWindowAttribute(h, 9, out fr, 16) != 0) fr = wr;
                int dl = fr.L - wr.L, dt = fr.T - wr.T, dr = wr.Rt - fr.Rt, db = wr.B - fr.B;
                ok = SetWindowPos(h, IntPtr.Zero, t.L - dl, t.T - dt, t.W + dl + dr, t.H + dt + db, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                if (!ok) break;
            }
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                return err == 5 ? "couldn't move it (it runs as administrator)" : "couldn't move it (error " + err + ")";
            }
            if (a.State == "Maximized") ShowWindow(h, SW_MAXIMIZE);
            else if (a.State == "Minimized") ShowWindow(h, SW_SHOWMINNOACTIVE);
            return null;
        }

        static bool Launch(WsApp a, Result res)
        {
            if (Demo.On || string.IsNullOrEmpty(a.LaunchPath)) return false;
            try
            {
                App.Trace("workspace: opening " + a.Name + " (" + a.LaunchPath + " " + a.LaunchArgs + ")");
                if (a.LaunchPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                    Process.Start("explorer.exe", a.LaunchPath);
                else
                {
                    var psi = new ProcessStartInfo(a.LaunchPath, a.LaunchArgs ?? "") { UseShellExecute = true };
                    try { string dir = Path.GetDirectoryName(a.LaunchPath); if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir; } catch { }
                    Process.Start(psi);
                }
                return true;
            }
            catch (Exception ex) { res.Problems.Add(a.Name + ": couldn't open (" + ex.Message + ")"); return false; }
        }

        // ---------------------------------------------------------------- sign-in and PowerToys import

        /// <summary>At sign-in (MacBar started within 10 minutes of Windows), run the workspace marked for it.</summary>
        public static void RunAtSignIn(Dispatcher ui)
        {
            var ws = All.FirstOrDefault(w => w.RunAtSignIn);
            if (ws == null || Demo.On) return;
            ulong uptime = 0; try { uptime = GetTickCount64(); } catch { }
            if (uptime > 10 * 60 * 1000) { App.Trace("workspace: not at sign-in (uptime " + uptime / 1000 + " s), skipping " + ws.Name); return; }
            var timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += delegate
            {
                timer.Stop();
                // Keep watching for 90 s: apps that start with Windows show their windows slowly.
                Run(ws, ws.SignInOpensApps, 90, ui, ShowResult);
            };
            timer.Start();
        }

        public static void ShowResult(Result r)
        {
            string text = r.Arranged + " window" + (r.Arranged == 1 ? "" : "s") + " arranged" + (r.Opened > 0 ? ", " + r.Opened + " app" + (r.Opened == 1 ? "" : "s") + " opened" : "");
            Toast.Show(r.Workspace, text, r.Problems);
        }

        public static string PowerToysFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\PowerToys\Workspaces\workspaces.json"); } }

        /// <summary>Converts PowerToys workspaces (window rects with invisible borders) into MacBar workspaces.</summary>
        public static List<Workspace> ImportPowerToys()
        {
            var result = new List<Workspace>();
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var root = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(PowerToysFile));
            var profiles = ChromeProfiles();
            foreach (Dictionary<string, object> pw in (System.Collections.ArrayList)root["workspaces"])
            {
                var ws = new Workspace { Name = (pw["name"] as string) ?? "Workspace" };
                var mons = new Dictionary<int, Dictionary<string, object>>();
                foreach (Dictionary<string, object> m in (System.Collections.ArrayList)pw["monitor-configuration"])
                    mons[Convert.ToInt32(m["monitor-number"])] = (Dictionary<string, object>)m["monitor-rect-dpi-aware"];
                foreach (Dictionary<string, object> pa in (System.Collections.ArrayList)pw["applications"])
                {
                    string path = pa["application-path"] as string ?? "", aumid = pa["app-user-model-id"] as string ?? "", pkg = pa["package-full-name"] as string ?? "";
                    string args = pa["command-line-arguments"] as string ?? "";
                    var pos = (Dictionary<string, object>)pa["position"];
                    Dictionary<string, object> mr;
                    if (!mons.TryGetValue(Convert.ToInt32(pa["monitor"]), out mr)) continue;
                    int ml = Convert.ToInt32(mr["left"]), mt = Convert.ToInt32(mr["top"]), mw = Convert.ToInt32(mr["width"]), mh = Convert.ToInt32(mr["height"]);
                    int x = Convert.ToInt32(pos["X"]), y = Convert.ToInt32(pos["Y"]), w = Convert.ToInt32(pos["width"]), h = Convert.ToInt32(pos["height"]);
                    // PowerToys stores the outer window rect. A 7 px overhang past a monitor edge is the invisible border.
                    bool bordered = (x < ml && ml - x <= 8) || (x + w > ml + mw && x + w - ml - mw <= 8);
                    if (bordered) { x += 7; w -= 14; h -= 7; }
                    var a = new WsApp
                    {
                        Name = pa["application"] as string, TitleHint = pa["title"] as string,
                        Key = Path.GetFileName(path).ToLowerInvariant(), LaunchPath = path, LaunchArgs = args,
                        State = Convert.ToBoolean(pa["maximized"]) ? "Maximized" : Convert.ToBoolean(pa["minimized"]) ? "Minimized" : "Normal",
                        MonL = ml, MonT = mt, MonW = mw, MonH = mh, X = x - ml, Y = y - mt, W = w, H = h
                    };
                    if (pkg.Length > 0 && aumid.Length > 0) { a.LaunchPath = "shell:AppsFolder\\" + aumid; a.LaunchArgs = ""; }
                    if (a.IsChrome) { string prof = Arg(args, "profile-directory"); SetChromeProfile(a, prof); }
                    if (a.Key.StartsWith("steam.exe") || path.EndsWith("Steam.exe", StringComparison.OrdinalIgnoreCase)) a.Key = "steam.exe";
                    a.Monitor = CurrentMonitors().Where(cm => cm.L == ml && cm.T == mt).Select(cm => cm.Device).FirstOrDefault() ?? "";
                    ws.Apps.Add(a);
                }
                result.Add(ws);
            }
            return result;
        }
    }
}
