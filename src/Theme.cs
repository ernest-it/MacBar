using System;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MacBar
{
    public static class Theme
    {
        public static bool Dark;
        public static readonly FontFamily Font = new FontFamily("Segoe UI Variable Text, Segoe UI");
        public static readonly Color Accent = Color.FromRgb(0x0A, 0x84, 0xFF);

        public static void Update(Settings s)
        {
            if (s.Theme == "Dark") Dark = true;
            else if (s.Theme == "Light") Dark = false;
            else
            {
                Dark = true;
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    {
                        object v = k == null ? null : k.GetValue("SystemUsesLightTheme");
                        if (v is int) Dark = (int)v == 0;
                    }
                }
                catch { }
            }
            if (Application.Current != null) Application.Current.Resources = Styles();
        }

        public static Color FgColor { get { return Dark ? Colors.White : Color.FromRgb(0x1d, 0x1d, 0x1f); } }
        public static SolidColorBrush Fg { get { return Frozen(FgColor); } }
        public static SolidColorBrush FgAlpha(byte a) { var c = FgColor; c.A = a; return Frozen(c); }

        public static SolidColorBrush BarTint(int opacityPct)
        {
            var c = Dark ? Color.FromRgb(28, 28, 30) : Color.FromRgb(246, 246, 246);
            c.A = (byte)Math.Round(Math.Max(0, Math.Min(100, opacityPct)) * 2.55);
            return Frozen(c);
        }

        public static SolidColorBrush PopupBg { get { return Frozen(Dark ? Color.FromArgb(0xF2, 0x2A, 0x2A, 0x2C) : Color.FromArgb(0xF5, 0xF6, 0xF6, 0xF6)); } }
        public static SolidColorBrush Hover { get { return FgAlpha(Dark ? (byte)0x30 : (byte)0x22); } }

        /// <summary>Subtle halo so icons and text stay readable over any wallpaper.</summary>
        public static Effect Halo()
        {
            var e = new DropShadowEffect
            {
                Color = Dark ? Colors.Black : Colors.White,
                BlurRadius = 5, ShadowDepth = 0, Opacity = Dark ? 0.6 : 0.75
            };
            e.Freeze();
            return e;
        }

        static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        /// <summary>0 = no blur, 1 = light blur, 2 = strong (acrylic) blur.</summary>
        public static void ApplyBlur(IntPtr hwnd, int level)
        {
            if (hwnd == IntPtr.Zero) return;
            if (level <= 0) Native.SetAccent(hwnd, 0, 0);
            else if (level == 1) Native.SetAccent(hwnd, 3, 0);
            else Native.SetAccent(hwnd, 4, Dark ? 0x10000000 : 0x10FFFFFF);
        }

        public static void SetRegion(IntPtr hwnd, int w, int h, int radius)
        {
            if (hwnd == IntPtr.Zero || w <= 0 || h <= 0) return;
            IntPtr rgn = radius > 0 ? Native.CreateRoundRectRgn(0, 0, w + 1, h + 1, radius * 2, radius * 2) : Native.CreateRectRgn(0, 0, w, h);
            Native.SetWindowRgn(hwnd, rgn, true); // the window owns the region afterwards
        }

        static string Hex(Color c) { return string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", c.A, c.R, c.G, c.B); }

        public static ResourceDictionary Styles()
        {
            string bg = Hex(PopupBg.Color);
            string fg = Hex(FgColor);
            string border = Dark ? "#40FFFFFF" : "#26000000";
            string sep = Dark ? "#30FFFFFF" : "#20000000";
            string dis = Dark ? "#70FFFFFF" : "#70000000";
            string xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
 <Style TargetType='ContextMenu'>
  <Setter Property='OverridesDefaultStyle' Value='True'/>
  <Setter Property='SnapsToDevicePixels' Value='True'/>
  <Setter Property='HasDropShadow' Value='False'/>
  <Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI'/>
  <Setter Property='FontSize' Value='13'/>
  <Setter Property='Template'><Setter.Value>
   <ControlTemplate TargetType='ContextMenu'>
    <Border Background='{BG}' BorderBrush='{BORDER}' BorderThickness='1' CornerRadius='8' Padding='5' Margin='6,2,10,12' MinWidth='180'>
     <Border.Effect><DropShadowEffect BlurRadius='14' ShadowDepth='4' Opacity='0.28'/></Border.Effect>
     <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
    </Border>
   </ControlTemplate>
  </Setter.Value></Setter>
 </Style>
 <Style TargetType='MenuItem'>
  <Setter Property='OverridesDefaultStyle' Value='True'/>
  <Setter Property='Foreground' Value='{FG}'/>
  <Setter Property='Template'><Setter.Value>
   <ControlTemplate TargetType='MenuItem'>
    <Border x:Name='Bd' Background='Transparent' CornerRadius='4' Padding='10,4,20,4'>
     <ContentPresenter ContentSource='Header' RecognizesAccessKey='False'/>
    </Border>
    <ControlTemplate.Triggers>
     <Trigger Property='IsHighlighted' Value='True'>
      <Setter TargetName='Bd' Property='Background' Value='#0A64D8'/>
      <Setter Property='Foreground' Value='White'/>
     </Trigger>
     <Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='{DIS}'/></Trigger>
    </ControlTemplate.Triggers>
   </ControlTemplate>
  </Setter.Value></Setter>
 </Style>
 <Style x:Key='{x:Static MenuItem.SeparatorStyleKey}' TargetType='Separator'>
  <Setter Property='Template'><Setter.Value>
   <ControlTemplate TargetType='Separator'><Border Height='1' Margin='10,4' Background='{SEP}'/></ControlTemplate>
  </Setter.Value></Setter>
 </Style>
 <Style TargetType='ToolTip'>
  <Setter Property='OverridesDefaultStyle' Value='True'/>
  <Setter Property='HasDropShadow' Value='False'/>
  <Setter Property='Template'><Setter.Value>
   <ControlTemplate TargetType='ToolTip'>
    <Border Background='{BG}' BorderBrush='{BORDER}' BorderThickness='1' CornerRadius='6' Padding='8,3' Margin='4'>
     <ContentPresenter TextElement.Foreground='{FG}' TextElement.FontSize='12' TextElement.FontFamily='Segoe UI Variable Text, Segoe UI'/>
    </Border>
   </ControlTemplate>
  </Setter.Value></Setter>
 </Style>
</ResourceDictionary>";
            xaml = xaml.Replace("{BG}", bg).Replace("{FG}", fg).Replace("{BORDER}", border).Replace("{SEP}", sep).Replace("{DIS}", dis);
            return (ResourceDictionary)XamlReader.Parse(xaml);
        }
    }

    /// <summary>Registers a window as a Windows "app bar" so maximized windows don't cover it.</summary>
    public class AppBar
    {
        const int ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3;
        public const int ABE_TOP = 1, ABE_BOTTOM = 3;
        public const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
        public readonly int CallbackMessage;
        IntPtr hwnd; bool registered; bool hasLast; RECT last;

        public AppBar() { CallbackMessage = Native.RegisterWindowMessage("MacBarAppBarMessage"); }
        public bool Registered { get { return registered; } }

        APPBARDATA Data()
        {
            var d = new APPBARDATA();
            d.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(APPBARDATA));
            d.hWnd = hwnd;
            return d;
        }

        public void Register(IntPtr h)
        {
            hwnd = h;
            if (registered) return;
            var d = Data(); d.uCallbackMessage = CallbackMessage;
            registered = Native.SHAppBarMessage(ABM_NEW, ref d) != IntPtr.Zero;
            hasLast = false;
        }

        public void Unregister()
        {
            if (!registered) return;
            var d = Data();
            Native.SHAppBarMessage(ABM_REMOVE, ref d);
            registered = false; hasLast = false;
        }

        public void Reset() { Unregister(); if (hwnd != IntPtr.Zero) Register(hwnd); }

        public RECT SetPos(int edge, RECT want, int thickness)
        {
            if (!registered) return want;
            var d = Data(); d.uEdge = edge; d.rc = want;
            Native.SHAppBarMessage(ABM_QUERYPOS, ref d);
            if (edge == ABE_TOP) d.rc.Bottom = d.rc.Top + thickness; else d.rc.Top = d.rc.Bottom - thickness;
            if (hasLast && d.rc.Left == last.Left && d.rc.Top == last.Top && d.rc.Right == last.Right && d.rc.Bottom == last.Bottom)
                return last;
            Native.SHAppBarMessage(ABM_SETPOS, ref d);
            App.Trace(string.Format("appbar SETPOS edge {0}: {1},{2}-{3},{4}", edge, d.rc.Left, d.rc.Top, d.rc.Right, d.rc.Bottom));
            last = d.rc; hasLast = true;
            return last;
        }
    }

    /// <summary>Hides Explorer's taskbar while MacBar runs and restores it afterwards.</summary>
    public static class TaskbarHider
    {
        const int ABM_GETSTATE = 4, ABM_SETSTATE = 10, ABS_AUTOHIDE = 1;
        static DispatcherTimer timer;
        static Settings settings;
        public static IntPtr ExcludeHwnd; // our stand-in tray window

        static IntPtr Real() { return Native.FindRealTray(ExcludeHwnd); }

        static int GetState()
        {
            var d = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(APPBARDATA)), hWnd = Real() };
            return Native.SHAppBarMessage(ABM_GETSTATE, ref d).ToInt32();
        }

        static void SetState(int state)
        {
            var d = new APPBARDATA { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(APPBARDATA)), hWnd = Real(), lParam = new IntPtr(state) };
            Native.SHAppBarMessage(ABM_SETSTATE, ref d);
        }

        public static void Hide(Settings s)
        {
            settings = s;
            if (!s.TaskbarHidden)
            {
                s.SavedTaskbarState = GetState();
                s.TaskbarHidden = true;
                s.Save();
            }
            // Auto-hide frees the screen space the taskbar reserves; hiding the window removes it visually.
            SetState(s.SavedTaskbarState | ABS_AUTOHIDE);
            HideWindows();
            if (timer == null)
            {
                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                timer.Tick += delegate { HideWindows(); };
                timer.Start();
            }
        }

        /// <summary>
        /// Collects all taskbar windows first. Showing/hiding while walking with FindWindowEx changes
        /// the Z-order and can make the walk loop forever on multi-monitor setups.
        /// </summary>
        static System.Collections.Generic.List<IntPtr> TaskbarWindows()
        {
            var list = new System.Collections.Generic.List<IntPtr>();
            foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            {
                IntPtr h = IntPtr.Zero;
                while ((h = Native.FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero && list.Count < 64)
                    if (h != ExcludeHwnd && !list.Contains(h)) list.Add(h);
            }
            return list;
        }

        public static void HideWindows()
        {
            foreach (var h in TaskbarWindows())
                if (Native.IsWindowVisible(h)) Native.ShowWindowAsync(h, Native.SW_HIDE);
        }

        public static void Restore(Settings s)
        {
            if (timer != null) { timer.Stop(); timer = null; }
            foreach (var h in TaskbarWindows()) Native.ShowWindowAsync(h, Native.SW_SHOWNA);
            if (s.TaskbarHidden)
            {
                SetState(s.SavedTaskbarState);
                s.TaskbarHidden = false;
                s.Save();
            }
        }
    }
}
