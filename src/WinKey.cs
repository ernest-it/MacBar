using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace MacBar
{
    /// <summary>
    /// Takes over a lone press of the Windows key (press and release with nothing else in between) so it
    /// opens MacBar's app page instead of the Windows Start menu. Win+E, Win+D, Win+Shift+S and other
    /// shortcuts keep working because only the release is touched, and only when no other key was pressed.
    /// The hook runs on its own thread so a busy UI thread can never make Windows skip it.
    /// </summary>
    public static class WinKey
    {
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vk, scan, flags, time; public UIntPtr extra; }

        const int WH_KEYBOARD_LL = 13;
        const uint KEYUP = 2;
        const byte VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_MASK = 0xE8; // 0xE8 is unassigned: a harmless "other key"
        static readonly UIntPtr Marker = new UIntPtr(0x4D414342); // tags our own injected keys

        static HookProc proc; // kept alive for the hook's lifetime
        static IntPtr hook;
        static uint threadId;
        static bool winDown, otherKey;
        static byte winVk;
        static Dispatcher ui;
        static Action onPress;

        public static void Start(Dispatcher dispatcher, Action pressed)
        {
            if (hook != IntPtr.Zero) return;
            ui = dispatcher; onPress = pressed;
            var t = new Thread(delegate ()
            {
                threadId = GetCurrentThreadId();
                proc = Hook;
                hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
                MSG m;
                while (GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
                if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
                hook = IntPtr.Zero;
            });
            t.IsBackground = true;
            t.Name = "MacBar Windows-key hook";
            t.Start();
        }

        public static void Stop()
        {
            if (threadId != 0) PostThreadMessage(threadId, 0x12 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        }

        static IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                if (k.extra == Marker) return CallNextHookEx(hook, code, wParam, lParam);
                int msg = wParam.ToInt32();
                bool down = msg == 0x100 || msg == 0x104, up = msg == 0x101 || msg == 0x105;
                bool isWin = k.vk == VK_LWIN || k.vk == VK_RWIN;
                if (isWin && down)
                {
                    if (!winDown) { winDown = true; otherKey = false; winVk = (byte)k.vk; }
                }
                else if (isWin && up && winDown)
                {
                    winDown = false;
                    if (!otherKey)
                    {
                        // Windows opens Start when the key is released with nothing pressed in between.
                        // Swallow this release and replay it after a dummy key, so Start stays closed.
                        Native.keybd_event(VK_MASK, 0, 0, Marker);
                        Native.keybd_event(VK_MASK, 0, KEYUP, Marker);
                        Native.keybd_event(winVk, 0, KEYUP, Marker);
                        ui.BeginInvoke(onPress);
                        return new IntPtr(1);
                    }
                }
                else if (down && winDown) otherKey = true;
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }
    }
}
