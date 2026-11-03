using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace MacBar
{
    public class TrayIcon
    {
        public IntPtr HWnd; public uint Id; public Guid Guid; public uint CallbackMessage; public uint Version;
        public ImageSource Icon; public string Tip; public bool Hidden;
    }

    /// <summary>
    /// Captures notification-area icons by owning a hidden, topmost window of class "Shell_TrayWnd".
    /// Shell_NotifyIcon delivers icons to the first such window, so we see every add/modify/delete and
    /// forward each message untouched to Explorer's real taskbar so nothing breaks.
    /// </summary>
    public class TrayHost
    {
        const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
        const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_STATE = 8, NIF_GUID = 0x20;

        public readonly List<TrayIcon> Icons = new List<TrayIcon>();
        public event Action Changed;
        public IntPtr Hwnd { get; private set; }

        WndProcDelegate proc; // kept alive for the native side
        DispatcherTimer timer;
        readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        bool changePending;
        static readonly int TaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

        public void Start()
        {
            proc = WndProc;
            IntPtr inst = Native.GetModuleHandle(null);
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf(typeof(WNDCLASSEX)), lpfnWndProc = proc, hInstance = inst, lpszClassName = "Shell_TrayWnd"
            };
            Native.RegisterClassEx(ref wc);
            Hwnd = Native.CreateWindowEx(Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW, "Shell_TrayWnd", "", Native.WS_POPUP,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (Hwnd == IntPtr.Zero) return;
            TaskbarHider.ExcludeHwnd = Hwnd;
            BringToTop();
            AskAppsToReAdd();

            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += delegate
            {
                // If Explorer's window got ahead of ours, reclaim the spot and ask apps to re-add.
                if (Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null) != Hwnd)
                {
                    BringToTop();
                    AskAppsToReAdd();
                }
                int removed = Icons.RemoveAll(i => !Native.IsWindow(i.HWnd));
                if (removed > 0) RaiseChanged();
            };
            timer.Start();
        }

        public void Stop()
        {
            if (timer != null) timer.Stop();
            if (Hwnd != IntPtr.Zero) Native.DestroyWindow(Hwnd);
            Hwnd = IntPtr.Zero;
            TaskbarHider.ExcludeHwnd = IntPtr.Zero;
        }

        void BringToTop()
        {
            Native.SetWindowPos(Hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void AskAppsToReAdd()
        {
            Native.SendNotifyMessage(Native.HWND_BROADCAST, TaskbarCreated, IntPtr.Zero, IntPtr.Zero);
        }

        IntPtr WndProc(IntPtr h, int msg, IntPtr w, IntPtr l)
        {
            if (msg == Native.WM_COPYDATA && l != IntPtr.Zero)
            {
                var cds = (COPYDATASTRUCT)Marshal.PtrToStructure(l, typeof(COPYDATASTRUCT));
                if (cds.dwData.ToInt64() == 1 && cds.lpData != IntPtr.Zero && cds.cbData >= 8 + 152)
                {
                    try { HandleNotify(cds.lpData, cds.cbData); } catch { }
                }
                IntPtr real = Native.FindRealTray(Hwnd);
                if (real != IntPtr.Zero)
                {
                    IntPtr result;
                    if (Native.SendMessageTimeout(real, msg, w, l, 2 /*ABORTIFHUNG*/, 3000, out result) != IntPtr.Zero)
                        return result;
                }
                return new IntPtr(1);
            }
            return Native.DefWindowProc(h, msg, w, l);
        }

        void HandleNotify(IntPtr data, int size)
        {
            if (Marshal.ReadInt32(data, 0) != 0x34753423) return;
            int message = Marshal.ReadInt32(data, 4);
            IntPtr nid = data + 8;
            int nidSize = size - 8;
            int cb = Marshal.ReadInt32(nid, 0);
            IntPtr hwnd = new IntPtr(Marshal.ReadInt32(nid, 4));
            uint id = (uint)Marshal.ReadInt32(nid, 8);
            uint flags = (uint)Marshal.ReadInt32(nid, 12);
            uint cbMsg = (uint)Marshal.ReadInt32(nid, 16);
            IntPtr hIcon = new IntPtr(Marshal.ReadInt32(nid, 20));
            bool v1 = cb <= 152;
            Guid guid = Guid.Empty;
            if ((flags & NIF_GUID) != 0 && nidSize >= 952 && cb >= 952)
            {
                var g = new byte[16];
                Marshal.Copy(nid + 936, g, 0, 16);
                guid = new Guid(g);
            }

            TrayIcon icon = Find(hwnd, id, guid);
            if (message == NIM_DELETE)
            {
                if (icon != null) { Icons.Remove(icon); RaiseChanged(); }
                return;
            }
            if (message == NIM_SETVERSION)
            {
                if (icon != null && !v1) icon.Version = (uint)Marshal.ReadInt32(nid, 800);
                return;
            }
            if (message != NIM_ADD && message != NIM_MODIFY) return;
            if (icon == null)
            {
                icon = new TrayIcon { HWnd = hwnd, Id = id, Guid = guid };
                Icons.Add(icon);
            }
            if (hwnd != IntPtr.Zero) icon.HWnd = hwnd;
            icon.Id = id;
            if ((flags & NIF_MESSAGE) != 0) icon.CallbackMessage = cbMsg;
            if ((flags & NIF_TIP) != 0) icon.Tip = ReadString(nid + 24, v1 ? 64 : 128);
            if ((flags & NIF_ICON) != 0 && hIcon != IntPtr.Zero)
            {
                IntPtr copy = Native.CopyIcon(hIcon);
                if (copy != IntPtr.Zero)
                {
                    var img = IconLoader.FromHIcon(copy);
                    Native.DestroyIcon(copy);
                    if (img != null) icon.Icon = img;
                }
            }
            if ((flags & NIF_STATE) != 0 && !v1)
            {
                uint state = (uint)Marshal.ReadInt32(nid, 280), mask = (uint)Marshal.ReadInt32(nid, 284);
                if ((mask & 1) != 0) icon.Hidden = (state & 1) != 0;
            }
            RaiseChanged();
        }

        static string ReadString(IntPtr p, int maxChars)
        {
            var chars = new char[maxChars];
            Marshal.Copy(p, chars, 0, maxChars);
            int n = Array.IndexOf(chars, '\0');
            return new string(chars, 0, n < 0 ? maxChars : n);
        }

        TrayIcon Find(IntPtr hwnd, uint id, Guid guid)
        {
            if (guid != Guid.Empty)
            {
                var byGuid = Icons.Find(i => i.Guid == guid);
                if (byGuid != null) return byGuid;
            }
            return Icons.Find(i => i.HWnd == hwnd && i.Id == id);
        }

        void RaiseChanged()
        {
            if (changePending) return;
            changePending = true;
            dispatcher.BeginInvoke(new Action(delegate
            {
                changePending = false;
                if (Changed != null) Changed();
            }), DispatcherPriority.Background);
        }

        static IntPtr MakeLong(int lo, int hi) { return new IntPtr((int)((uint)(ushort)lo | ((uint)(ushort)hi << 16))); }

        /// <summary>Sends a click to the icon's owner the same way Explorer would.</summary>
        public void Click(TrayIcon icon, bool right, bool doubleClick)
        {
            if (!Native.IsWindow(icon.HWnd)) { Icons.Remove(icon); RaiseChanged(); return; }
            Native.AllowSetForegroundWindow(Native.GetPid(icon.HWnd));
            POINT pt; Native.GetCursorPos(out pt);
            int cbMsg = (int)icon.CallbackMessage;
            if (cbMsg == 0) return;
            Action<int> send = delegate (int m)
            {
                if (icon.Version >= 4)
                    Native.PostMessage(icon.HWnd, cbMsg, MakeLong(pt.X, pt.Y), MakeLong(m, (int)icon.Id));
                else
                    Native.PostMessage(icon.HWnd, cbMsg, new IntPtr((int)icon.Id), new IntPtr(m));
            };
            if (right)
            {
                send(Native.WM_RBUTTONDOWN); send(Native.WM_RBUTTONUP);
                if (icon.Version >= 4) send(Native.WM_CONTEXTMENU);
            }
            else if (doubleClick)
            {
                send(Native.WM_LBUTTONDBLCLK); send(Native.WM_LBUTTONUP);
            }
            else
            {
                send(Native.WM_LBUTTONDOWN); send(Native.WM_LBUTTONUP);
                if (icon.Version >= 4) send(0x400 /* NIN_SELECT */);
            }
        }
    }
}
