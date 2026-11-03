using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.Linq;
using System.Threading;
using System.Windows.Threading;

namespace MacBar
{
    public class SettingsPage
    {
        public string Name, Uri, Keywords;
    }

    /// <summary>Windows Settings pages, searchable by name and common keywords. Purely local.</summary>
    public static class SettingsCatalog
    {
        public static readonly List<SettingsPage> Pages = new List<SettingsPage>();

        static void P(string name, string uri, string keywords) { Pages.Add(new SettingsPage { Name = name, Uri = "ms-settings:" + uri, Keywords = keywords }); }

        static SettingsCatalog()
        {
            P("Settings", "", "control panel preferences options");
            P("Display", "display", "screen resolution scale brightness monitor refresh rate hdr night light");
            P("Night light", "nightlight", "blue light warm color temperature");
            P("Sound", "sound", "audio volume speakers output input microphone");
            P("Sound devices", "sound-devices", "audio output input speakers headphones");
            P("Volume mixer", "apps-volume", "app volume audio levels");
            P("Notifications", "notifications", "alerts do not disturb focus banners");
            P("Focus", "quiethours", "do not disturb focus assist quiet hours");
            P("Power & battery", "powersleep", "power sleep battery saver energy screen timeout shutdown");
            P("Battery saver", "batterysaver", "power battery energy");
            P("Storage", "storagesense", "disk space drive cleanup storage sense");
            P("Multitasking", "multitasking", "snap windows virtual desktops alt tab");
            P("Clipboard", "clipboard", "copy paste history");
            P("Remote Desktop", "remotedesktop", "rdp remote access");
            P("About this PC", "about", "system info specs device name rename pc windows version processor ram");
            P("Bluetooth & devices", "bluetooth", "bluetooth pair devices headphones");
            P("Printers & scanners", "printers", "printer scanner print");
            P("Mouse", "mousetouchpad", "mouse pointer cursor scroll speed buttons");
            P("Touchpad", "devices-touchpad", "trackpad gestures scroll tap");
            P("Typing", "typing", "keyboard autocorrect suggestions spelling");
            P("Pen & Windows Ink", "pen", "pen stylus ink");
            P("AutoPlay", "autoplay", "usb removable media");
            P("USB", "usb", "usb devices");
            P("Wi-Fi", "network-wifi", "wireless network internet wlan");
            P("Network & internet", "network", "internet ethernet connection status");
            P("Ethernet", "network-ethernet", "wired network lan");
            P("VPN", "network-vpn", "vpn virtual private network");
            P("Mobile hotspot", "network-mobilehotspot", "hotspot tethering share internet");
            P("Airplane mode", "network-airplanemode", "flight mode wireless off");
            P("Proxy", "network-proxy", "proxy network");
            P("Personalization", "personalization", "theme look appearance customize");
            P("Background", "personalization-background", "wallpaper desktop picture slideshow");
            P("Colors", "colors", "accent color dark mode light mode transparency theme");
            P("Themes", "themes", "theme desktop icons");
            P("Lock screen", "lockscreen", "lock screen picture");
            P("Fonts", "fonts", "font typeface");
            P("Start", "personalization-start", "start menu");
            P("Taskbar", "taskbar", "taskbar");
            P("Installed apps", "appsfeatures", "uninstall programs remove apps features");
            P("Default apps", "defaultapps", "default browser file associations open with");
            P("Startup apps", "startupapps", "startup login launch boot");
            P("Optional features", "optionalfeatures", "features add windows features");
            P("Apps for websites", "appsforwebsites", "links web apps");
            P("Accounts", "accounts", "account user profile microsoft account");
            P("Your info", "yourinfo", "account picture profile name");
            P("Sign-in options", "signinoptions", "password pin windows hello fingerprint face login");
            P("Email & accounts", "emailandaccounts", "email mail accounts");
            P("Family & other users", "otherusers", "users family add user");
            P("Windows backup", "backup", "backup onedrive restore");
            P("Date & time", "dateandtime", "clock time zone date sync");
            P("Language & region", "regionlanguage", "language region locale format keyboard layout");
            P("Speech", "speech", "voice speech recognition");
            P("Gaming", "gaming", "game mode xbox game bar");
            P("Game Mode", "gaming-gamemode", "games performance");
            P("Captures", "gaming-gamedvr", "record screen recording game clips");
            P("Accessibility", "easeofaccess", "ease of access vision hearing");
            P("Text size", "easeofaccess-display", "text size bigger larger font");
            P("Mouse pointer and touch", "easeofaccess-mousepointer", "cursor size pointer color");
            P("Narrator", "easeofaccess-narrator", "screen reader narrator");
            P("Magnifier", "easeofaccess-magnifier", "zoom magnify");
            P("Color filters", "easeofaccess-colorfilter", "colorblind grayscale invert");
            P("Captions", "easeofaccess-closedcaptioning", "subtitles captions");
            P("Keyboard (accessibility)", "easeofaccess-keyboard", "sticky keys on-screen keyboard filter keys");
            P("Privacy & security", "privacy", "privacy permissions security");
            P("Windows Security", "windowsdefender", "antivirus defender virus firewall security");
            P("Location", "privacy-location", "gps location services");
            P("Camera privacy", "privacy-webcam", "camera webcam permissions");
            P("Microphone privacy", "privacy-microphone", "microphone mic permissions");
            P("Find my device", "findmydevice", "lost device locate");
            P("For developers", "developers", "developer mode sudo");
            P("Windows Update", "windowsupdate", "update upgrade patches check for updates");
            P("Update history", "windowsupdate-history", "updates installed history");
            P("Advanced update options", "windowsupdate-options", "active hours restart updates");
            P("Recovery", "recovery", "reset this pc restore advanced startup");
            P("Activation", "activation", "product key license activate");
            P("Troubleshoot", "troubleshoot", "fix problems troubleshooter");
        }

        public static List<SettingsPage> Search(string q, int max)
        {
            q = q.Trim();
            if (q.Length == 0) return new List<SettingsPage>();
            return Pages.Select(p => new { p, r = Rank(p, q) }).Where(x => x.r < 4).OrderBy(x => x.r).ThenBy(x => x.p.Name.Length)
                        .Take(max).Select(x => x.p).ToList();
        }

        static int Rank(SettingsPage p, string q)
        {
            var c = StringComparison.CurrentCultureIgnoreCase;
            if (p.Name.StartsWith(q, c)) return 0;
            if (p.Name.Split(' ', '-', '&').Any(w => w.StartsWith(q, c))) return 1;
            if (q.Length >= 3 && p.Name.IndexOf(q, c) >= 0) return 2;
            if (q.Length >= 3 && p.Keywords.Split(' ').Any(w => w.StartsWith(q, c))) return 3;
            return 4;
        }
    }

    public class FileHit { public string Name, Path; public bool IsFolder; }

    /// <summary>
    /// Searches files and folders in the user's profile through the local Windows Search index
    /// (the same index Explorer uses). Never touches the internet. Runs on a background thread;
    /// only the newest query's results are delivered.
    /// </summary>
    public static class FileSearch
    {
        static int generation;

        public static void Query(string q, int max, Dispatcher ui, Action<string, List<FileHit>> done)
        {
            int gen = Interlocked.Increment(ref generation);
            q = q.Trim();
            if (q.Length < 2) { done(q, new List<FileHit>()); return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(120); // debounce while typing
                if (gen != generation) return;
                var hits = new List<FileHit>();
                try
                {
                    string esc = q.Replace("'", "''").Replace("%", "[%]").Replace("_", "[_]");
                    string scope = "file:" + Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/');
                    string sql = "SELECT TOP " + (max * 3) + " System.ItemPathDisplay, System.ItemNameDisplay, System.ItemType FROM SystemIndex " +
                                 "WHERE SCOPE='" + scope + "' AND System.FileName LIKE '%" + esc + "%' " +
                                 "AND System.ItemType <> '.lnk' AND System.FileAttributes <> ALL BITWISE 2 " +
                                 "ORDER BY System.DateModified DESC";
                    using (var con = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';"))
                    {
                        con.Open();
                        using (var cmd = new OleDbCommand(sql, con))
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read() && hits.Count < max * 3)
                            {
                                string path = r[0] as string, name = r[1] as string, type = r[2] as string;
                                if (string.IsNullOrEmpty(path) || path.IndexOf("\\AppData\\", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                if (path.IndexOf("\\.", StringComparison.Ordinal) >= 0 || path.IndexOf("\\node_modules\\", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                                hits.Add(new FileHit { Path = path, Name = name ?? System.IO.Path.GetFileName(path), IsFolder = type == "Directory" || type == "File folder" });
                            }
                        }
                    }
                }
                catch (Exception ex) { App.Trace("file search failed: " + ex.Message); }
                // Names that start with the query first, then the rest (each group newest first).
                var c = StringComparison.CurrentCultureIgnoreCase;
                hits = hits.Where(h => h.Name.StartsWith(q, c)).Concat(hits.Where(h => !h.Name.StartsWith(q, c))).Take(max).ToList();
                if (gen != generation) return;
                ui.BeginInvoke(new Action(delegate { if (gen == generation) done(q, hits); }));
            });
        }
    }
}
