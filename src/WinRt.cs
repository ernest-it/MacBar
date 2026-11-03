using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Devices.Radios;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace MacBar
{
    /// <summary>
    /// Lets C# `await` WinRT async operations. The framework's own helper (System.Runtime.WindowsRuntime)
    /// needs the SDK's merged Windows.winmd, which plain Windows installs don't have.
    /// </summary>
    public static class WinRtAwait
    {
        public static TaskAwaiter<T> GetAwaiter<T>(this IAsyncOperation<T> op)
        {
            var tcs = new TaskCompletionSource<T>();
            op.Completed = delegate (IAsyncOperation<T> o, AsyncStatus st)
            {
                if (st == AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
                else tcs.TrySetException(o.ErrorCode ?? new Exception("WinRT operation " + st));
            };
            return tcs.Task.GetAwaiter();
        }
    }

    [ComImport, Guid("DB3AFBFB-08E6-46C6-AA70-BF9A34C30AB7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IRadioManager
    {
        [PreserveSig] int IsRMSupported(out uint state);
        [PreserveSig] int GetUIRadioInstances([MarshalAs(UnmanagedType.IUnknown)] out object coll);
        [PreserveSig] int GetSystemRadioState(out int enabled, out int p2, out int reason);
        [PreserveSig] int SetSystemRadioState(int enabled);
    }

    /// <summary>Wi-Fi / Bluetooth radios (Windows.Devices.Radios) and airplane mode (the RadioManagement API used by Quick Settings).</summary>
    public static class Radios
    {
        public static async Task<Radio> Get(RadioKind kind)
        {
            try
            {
                await Radio.RequestAccessAsync();
                foreach (var r in await Radio.GetRadiosAsync()) if (r.Kind == kind) return r;
            }
            catch { }
            return null;
        }

        public static async Task<bool> Set(Radio r, bool on)
        {
            if (r == null) return false;
            try { return await r.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed; }
            catch { return false; }
        }

        static IRadioManager Manager()
        {
            return (IRadioManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("581333F6-28DB-41BE-BC7A-FF201F12F3F6")));
        }

        /// <summary>null if airplane mode isn't available on this PC.</summary>
        public static bool? AirplaneMode()
        {
            try
            {
                var rm = Manager();
                int enabled, p2, reason;
                int hr = rm.GetSystemRadioState(out enabled, out p2, out reason);
                Marshal.ReleaseComObject(rm);
                if (hr != 0) return null;
                return enabled == 0; // "system radios enabled" == airplane mode off
            }
            catch { return null; }
        }

        public static bool SetAirplaneMode(bool on)
        {
            try
            {
                var rm = Manager();
                int hr = rm.SetSystemRadioState(on ? 0 : 1);
                Marshal.ReleaseComObject(rm);
                return hr == 0;
            }
            catch { return false; }
        }
    }

    public class NowPlaying
    {
        public string Title, Artist, AppId; public bool Playing; public ImageSource Art;
        public double Position, Duration; public DateTime PositionAt; public bool CanSeek;

        /// <summary>"SpotifyAB.SpotifyMusic_xxx!Spotify" → "Spotify"; "Chrome" → "Chrome".</summary>
        public string AppName
        {
            get
            {
                if (string.IsNullOrEmpty(AppId)) return "";
                string s = AppId.Contains("!") ? AppId.Substring(AppId.LastIndexOf('!') + 1) : AppId;
                if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
                if (s.Equals("App", StringComparison.OrdinalIgnoreCase) && AppId.Contains("_")) s = AppId.Substring(0, AppId.IndexOf('_')).Split('.').Last();
                return s.Length > 0 ? char.ToUpper(s[0]) + s.Substring(1) : s;
            }
        }

        /// <summary>Current position, extrapolated while playing (apps only report it every few seconds).</summary>
        public double LivePosition
        {
            get
            {
                double p = Position + (Playing ? (DateTime.UtcNow - PositionAt).TotalSeconds : 0);
                return Duration > 0 ? Math.Max(0, Math.Min(Duration, p)) : Math.Max(0, p);
            }
        }

        public bool SameTrack(NowPlaying o) { return o != null && o.Title == Title && o.Artist == Artist && o.AppId == AppId; }
    }

    /// <summary>Media currently playing in any app (Spotify, browsers, ...), via the system media transport controls.</summary>
    public static class Media
    {
        static GlobalSystemMediaTransportControlsSessionManager mgr;

        static async Task<GlobalSystemMediaTransportControlsSession> Session()
        {
            try
            {
                if (mgr == null) mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                return mgr.GetCurrentSession();
            }
            catch { return null; }
        }

        /// <summary>Current media. Pass the previous result to reuse its album art when the track hasn't changed.</summary>
        public static async Task<NowPlaying> Current(NowPlaying previous = null)
        {
            if (Demo.On) return Demo.Track();
            var s = await Session();
            if (s == null) return null;
            try
            {
                var p = await s.TryGetMediaPropertiesAsync();
                var info = s.GetPlaybackInfo();
                var np = new NowPlaying
                {
                    Title = p.Title, Artist = p.Artist, AppId = s.SourceAppUserModelId,
                    Playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    CanSeek = info.Controls.IsPlaybackPositionEnabled
                };
                try
                {
                    var tl = s.GetTimelineProperties();
                    np.Duration = (tl.EndTime - tl.StartTime).TotalSeconds;
                    np.Position = (tl.Position - tl.StartTime).TotalSeconds;
                    np.PositionAt = tl.LastUpdatedTime.UtcDateTime;
                    if (np.PositionAt.Year < 2000 || np.PositionAt > DateTime.UtcNow) np.PositionAt = DateTime.UtcNow;
                }
                catch { np.PositionAt = DateTime.UtcNow; }
                if (np.SameTrack(previous) && previous.Art != null) { np.Art = previous.Art; return np; }
                if (p.Thumbnail != null)
                {
                    try
                    {
                        var stream = await p.Thumbnail.OpenReadAsync();
                        var reader = new DataReader(stream.GetInputStreamAt(0));
                        uint size = (uint)stream.Size;
                        await reader.LoadAsync(size);
                        var bytes = new byte[size];
                        reader.ReadBytes(bytes);
                        var bmp = new BitmapImage();
                        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.DecodePixelWidth = 96;
                        bmp.StreamSource = new MemoryStream(bytes); bmp.EndInit(); bmp.Freeze();
                        np.Art = bmp;
                    }
                    catch { }
                }
                return np;
            }
            catch { return null; }
        }

        public static async Task Seek(double seconds)
        {
            if (Demo.On) return;
            var s = await Session();
            if (s == null) return;
            try
            {
                var tl = s.GetTimelineProperties();
                await s.TryChangePlaybackPositionAsync((tl.StartTime + TimeSpan.FromSeconds(seconds)).Ticks);
            }
            catch { }
        }

        public static async Task Command(int which) // 0 prev, 1 play/pause, 2 next
        {
            if (Demo.On) { if (which == 1) Demo.Paused = !Demo.Paused; return; }
            var s = await Session();
            if (s == null) return;
            try
            {
                if (which == 0) await s.TrySkipPreviousAsync();
                else if (which == 1) await s.TryTogglePlayPauseAsync();
                else await s.TrySkipNextAsync();
            }
            catch { }
        }
    }

    /// <summary>
    /// Primary display brightness: laptop panels via WMI, external monitors via DDC/CI.
    /// DDC calls take ~50 ms, so writes run on a background thread and only the latest value is sent.
    /// </summary>
    public static class Brightness
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR { public IntPtr h; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string desc; }
        [DllImport("dxva2.dll")] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr m, out uint n);
        [DllImport("dxva2.dll")] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr m, uint n, [Out] PHYSICAL_MONITOR[] arr);
        [DllImport("dxva2.dll")] static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
        [DllImport("dxva2.dll")] static extern bool SetMonitorBrightness(IntPtr h, uint value);
        [DllImport("dxva2.dll")] static extern bool DestroyPhysicalMonitors(uint n, PHYSICAL_MONITOR[] arr);

        static int pending = -1;
        static readonly object gate = new object();
        static bool writerRunning;

        static T WithMonitor<T>(Func<IntPtr, T> f, T fallback)
        {
            IntPtr mon = Native.MonitorFromPoint(new POINT(), 1);
            uint n;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(mon, out n) || n == 0) return fallback;
            var arr = new PHYSICAL_MONITOR[n];
            if (!GetPhysicalMonitorsFromHMONITOR(mon, n, arr)) return fallback;
            try { return f(arr[0].h); }
            finally { DestroyPhysicalMonitors(n, arr); }
        }

        /// <summary>0-100, or -1 if the display can't be controlled. Call off the UI thread.</summary>
        public static int Read()
        {
            try
            {
                using (var s = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness"))
                    foreach (ManagementObject o in s.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
            }
            catch { }
            return WithMonitor(h =>
            {
                uint min, cur, max;
                if (!GetMonitorBrightness(h, out min, out cur, out max) || max <= min) return -1;
                return (int)Math.Round(100.0 * (cur - min) / (max - min));
            }, -1);
        }

        public static void Write(int value)
        {
            lock (gate)
            {
                pending = Math.Max(0, Math.Min(100, value));
                if (writerRunning) return;
                writerRunning = true;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                while (true)
                {
                    int v;
                    lock (gate)
                    {
                        if (pending < 0) { writerRunning = false; return; }
                        v = pending; pending = -1;
                    }
                    if (!WriteWmi(v))
                        WithMonitor(h =>
                        {
                            uint min, cur, max;
                            if (GetMonitorBrightness(h, out min, out cur, out max))
                                SetMonitorBrightness(h, (uint)(min + (max - min) * v / 100));
                            return 0;
                        }, 0);
                }
            });
        }

        static bool WriteWmi(int v)
        {
            try
            {
                using (var s = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM WmiMonitorBrightnessMethods"))
                    foreach (ManagementObject o in s.Get()) { o.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)v }); return true; }
            }
            catch { }
            return false;
        }
    }
}
