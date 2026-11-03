using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace MacBar
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore props);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr n);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr n);
        [PreserveSig] int GetChannelCount(out uint c);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    public class AudioController
    {
        public bool Available; public float Volume; public bool Muted; public string DeviceName = "";
        IMMDeviceEnumerator enumerator;
        IAudioEndpointVolume endpoint;
        string deviceId;
        Guid ctx = Guid.Empty;
        static readonly PROPERTYKEY FriendlyName = new PROPERTYKEY(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

        /// <summary>Reads current state; returns true if anything changed.</summary>
        public bool Poll()
        {
            bool oldAvail = Available; float oldVol = Volume; bool oldMute = Muted;
            try
            {
                if (enumerator == null) enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                IMMDevice dev;
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null)
                {
                    Release(); Available = false;
                }
                else
                {
                    string id; dev.GetId(out id);
                    if (id != deviceId || endpoint == null)
                    {
                        Release();
                        Guid iid = typeof(IAudioEndpointVolume).GUID; object o;
                        if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) == 0) endpoint = (IAudioEndpointVolume)o;
                        deviceId = id;
                        DeviceName = "";
                        IPropertyStore ps;
                        if (dev.OpenPropertyStore(0, out ps) == 0 && ps != null)
                        {
                            DeviceName = Native.ReadString(ps, FriendlyName) ?? "";
                            Marshal.ReleaseComObject(ps);
                        }
                    }
                    Marshal.ReleaseComObject(dev);
                    if (endpoint != null)
                    {
                        float v; bool m;
                        endpoint.GetMasterVolumeLevelScalar(out v);
                        endpoint.GetMute(out m);
                        Volume = v; Muted = m; Available = true;
                    }
                    else Available = false;
                }
            }
            catch { Release(); Available = false; }
            return oldAvail != Available || Math.Abs(oldVol - Volume) > 0.001 || oldMute != Muted;
        }

        void Release()
        {
            if (endpoint != null) { try { Marshal.ReleaseComObject(endpoint); } catch { } endpoint = null; }
            deviceId = null;
        }

        public void SetVolume(float v)
        {
            if (endpoint == null) return;
            v = Math.Max(0f, Math.Min(1f, v));
            endpoint.SetMasterVolumeLevelScalar(v, ref ctx);
            if (Muted && v > 0) endpoint.SetMute(false, ref ctx);
            Poll();
        }

        public void SetMute(bool m)
        {
            if (endpoint == null) return;
            endpoint.SetMute(m, ref ctx);
            Poll();
        }
    }

    public class NetworkState
    {
        public bool WifiConnected; public int Signal; public string Ssid = "";
        public bool Ethernet; public bool HasWifiAdapter;

        public bool Poll()
        {
            bool oc = WifiConnected, oe = Ethernet; int os = Signal; string oss = Ssid;
            WifiConnected = false; Signal = 0; Ssid = ""; HasWifiAdapter = false;
            ReadWlan();
            Ethernet = false;
            if (!WifiConnected)
            {
                try
                {
                    Ethernet = NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                        n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType == NetworkInterfaceType.Ethernet &&
                        n.GetIPProperties().GatewayAddresses.Count > 0);
                }
                catch { }
            }
            return oc != WifiConnected || oe != Ethernet || os != Signal || oss != Ssid;
        }

        void ReadWlan()
        {
            IntPtr h; int ver;
            try { if (Native.WlanOpenHandle(2, IntPtr.Zero, out ver, out h) != 0) return; }
            catch { return; }
            try
            {
                IntPtr list;
                if (Native.WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) return;
                try
                {
                    int count = Marshal.ReadInt32(list, 0);
                    HasWifiAdapter = count > 0;
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr item = list + 8 + i * 532;
                        var g = new byte[16]; Marshal.Copy(item, g, 0, 16);
                        Guid iface = new Guid(g);
                        int state = Marshal.ReadInt32(item, 528);
                        if (state != 1) continue; // wlan_interface_state_connected
                        int size; IntPtr data;
                        if (Native.WlanQueryInterface(h, ref iface, 7, IntPtr.Zero, out size, out data, IntPtr.Zero) != 0) continue;
                        try
                        {
                            int len = Math.Min(32, Marshal.ReadInt32(data, 520));
                            var ssid = new byte[len]; Marshal.Copy(data + 524, ssid, 0, len);
                            Ssid = Encoding.UTF8.GetString(ssid);
                            Signal = Marshal.ReadInt32(data, 576);
                            WifiConnected = true;
                        }
                        finally { Native.WlanFreeMemory(data); }
                        break;
                    }
                }
                finally { Native.WlanFreeMemory(list); }
            }
            finally { Native.WlanCloseHandle(h, IntPtr.Zero); }
        }
    }

    public class WifiNetwork
    {
        public string Ssid, Profile; public int Signal; public bool Secured, Connected, HasProfile;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WLAN_CONNECTION_PARAMETERS
    {
        public int Mode; [MarshalAs(UnmanagedType.LPWStr)] public string Profile;
        public IntPtr Ssid, BssidList; public int BssType; public int Flags;
    }

    /// <summary>Wi-Fi control through the native WLAN API (lists, scans, connects with saved profiles, radio on/off).</summary>
    public static class WifiControl
    {
        [DllImport("wlanapi.dll")] static extern int WlanGetAvailableNetworkList(IntPtr h, ref Guid iface, int flags, IntPtr res, out IntPtr list);
        [DllImport("wlanapi.dll")] static extern int WlanScan(IntPtr h, ref Guid iface, IntPtr ssid, IntPtr ie, IntPtr res);
        [DllImport("wlanapi.dll")] static extern int WlanConnect(IntPtr h, ref Guid iface, ref WLAN_CONNECTION_PARAMETERS p, IntPtr res);
        [DllImport("wlanapi.dll")] static extern int WlanDisconnect(IntPtr h, ref Guid iface, IntPtr res);
        [DllImport("wlanapi.dll")] static extern int WlanSetInterface(IntPtr h, ref Guid iface, int opcode, int size, IntPtr data, IntPtr res);

        static T With<T>(Func<IntPtr, Guid, T> f, T fallback)
        {
            IntPtr h; int ver;
            try { if (Native.WlanOpenHandle(2, IntPtr.Zero, out ver, out h) != 0) return fallback; }
            catch { return fallback; }
            try
            {
                IntPtr list;
                if (Native.WlanEnumInterfaces(h, IntPtr.Zero, out list) != 0) return fallback;
                Guid iface;
                try
                {
                    if (Marshal.ReadInt32(list, 0) < 1) return fallback;
                    var g = new byte[16]; Marshal.Copy(list + 8, g, 0, 16); iface = new Guid(g);
                }
                finally { Native.WlanFreeMemory(list); }
                return f(h, iface);
            }
            finally { Native.WlanCloseHandle(h, IntPtr.Zero); }
        }

        public static bool HasAdapter { get { return With((h, g) => true, false); } }

        public static void Scan() { With((h, g) => WlanScan(h, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), 0); }

        public static System.Collections.Generic.List<WifiNetwork> Networks()
        {
            return With((h, g) =>
            {
                var result = new System.Collections.Generic.List<WifiNetwork>();
                IntPtr list;
                if (WlanGetAvailableNetworkList(h, ref g, 0, IntPtr.Zero, out list) != 0) return result;
                try
                {
                    int n = Marshal.ReadInt32(list, 0);
                    for (int i = 0; i < n; i++)
                    {
                        IntPtr p = list + 8 + i * 628;
                        int len = Math.Min(32, Marshal.ReadInt32(p, 512));
                        if (len == 0) continue; // hidden network
                        var ssid = new byte[len]; Marshal.Copy(p + 516, ssid, 0, len);
                        int flags = Marshal.ReadInt32(p, 620);
                        var net = new WifiNetwork
                        {
                            Ssid = Encoding.UTF8.GetString(ssid), Profile = Marshal.PtrToStringUni(p),
                            Signal = Marshal.ReadInt32(p, 604), Secured = Marshal.ReadInt32(p, 608) != 0,
                            Connected = (flags & 1) != 0, HasProfile = (flags & 2) != 0
                        };
                        var existing = result.FirstOrDefault(x => x.Ssid == net.Ssid);
                        if (existing == null) result.Add(net);
                        else
                        {
                            existing.Connected |= net.Connected;
                            existing.Signal = Math.Max(existing.Signal, net.Signal);
                            if (net.HasProfile && !existing.HasProfile) { existing.HasProfile = true; existing.Profile = net.Profile; }
                        }
                    }
                }
                finally { Native.WlanFreeMemory(list); }
                return result.OrderByDescending(x => x.Connected).ThenByDescending(x => x.HasProfile).ThenByDescending(x => x.Signal).ToList();
            }, new System.Collections.Generic.List<WifiNetwork>());
        }

        /// <summary>Connects using a saved profile. Returns false if there is none (caller should open Windows' UI).</summary>
        public static bool Connect(WifiNetwork n)
        {
            if (!n.HasProfile || string.IsNullOrEmpty(n.Profile)) return false;
            return With((h, g) =>
            {
                var p = new WLAN_CONNECTION_PARAMETERS { Mode = 0, Profile = n.Profile, BssType = 1 };
                return WlanConnect(h, ref g, ref p, IntPtr.Zero) == 0;
            }, false);
        }

        public static void Disconnect() { With((h, g) => WlanDisconnect(h, ref g, IntPtr.Zero), 0); }

        /// <summary>True if the Wi-Fi radio is on (software and hardware).</summary>
        public static bool RadioOn()
        {
            return With((h, g) =>
            {
                int size; IntPtr data;
                if (Native.WlanQueryInterface(h, ref g, 4, IntPtr.Zero, out size, out data, IntPtr.Zero) != 0) return true;
                try
                {
                    int phys = Marshal.ReadInt32(data, 0);
                    for (int i = 0; i < phys; i++)
                    {
                        int sw = Marshal.ReadInt32(data, 4 + i * 12 + 4), hw = Marshal.ReadInt32(data, 4 + i * 12 + 8);
                        if (sw == 1 && hw == 1) return true;
                    }
                    return false;
                }
                finally { Native.WlanFreeMemory(data); }
            }, false);
        }

        /// <summary>Turns the radio on/off. Returns false if Windows refused (e.g. needs admin).</summary>
        public static bool SetRadio(bool on)
        {
            return With((h, g) =>
            {
                IntPtr p = Marshal.AllocHGlobal(12);
                try
                {
                    Marshal.WriteInt32(p, 0, 0);            // dwPhyIndex
                    Marshal.WriteInt32(p, 4, on ? 1 : 2);   // software state
                    Marshal.WriteInt32(p, 8, on ? 1 : 2);   // hardware state (ignored on set)
                    return WlanSetInterface(h, ref g, 4, 12, p, IntPtr.Zero) == 0;
                }
                finally { Marshal.FreeHGlobal(p); }
            }, false);
        }
    }

    public class BatteryState
    {
        public bool Present; public int Percent; public bool Charging; public bool OnAc;

        public bool Poll()
        {
            bool op = Present, oc = Charging, oa = OnAc; int opct = Percent;
            SYSTEM_POWER_STATUS s;
            if (Native.GetSystemPowerStatus(out s))
            {
                Present = s.BatteryFlag != 128 && s.BatteryFlag != 255 && s.BatteryLifePercent != 255;
                Percent = Math.Min(100, (int)s.BatteryLifePercent);
                OnAc = s.ACLineStatus == 1;
                Charging = (s.BatteryFlag & 8) != 0 || (OnAc && Percent < 100);
            }
            else Present = false;
            return op != Present || oc != Charging || oa != OnAc || opct != Percent;
        }
    }
}
