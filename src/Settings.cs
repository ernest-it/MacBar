using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace MacBar
{
    public class PinnedApp
    {
        public string Target { get; set; }   // .lnk, .exe or shell:AppsFolder\<AUMID>
        public string Aumid { get; set; }
        public string ExePath { get; set; }
        public string Name { get; set; }
    }

    public class Settings
    {
        public string Theme { get; set; }          // Auto | Light | Dark
        public int Opacity { get; set; }           // 0-100 tint opacity for both bars
        public int Blur { get; set; }              // 0 none, 1 light, 2 strong
        public int IconSize { get; set; }          // dock icon size (DIP)
        public bool DockReservesSpace { get; set; }    // maximized windows stop above the dock (only when auto-hide is off)
        public int DockAutoHide { get; set; }          // 0 never, 1 always (macOS), 2 when a window overlaps (intellihide)
        public int SettingsVersion { get; set; }
        public bool MenuBarReservesSpace { get; set; } // maximized windows stop below the menu bar
        public bool ShowTrayIcons { get; set; }
        public bool ShowBatteryPercent { get; set; }
        public bool HasLocation { get; set; }
        public string WeatherLocation { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public bool Fahrenheit { get; set; }
        public List<PinnedApp> Pinned { get; set; }
        public bool PinsImported { get; set; }
        // Remembers the taskbar's auto-hide state so it can be restored even after a crash.
        public bool TaskbarHidden { get; set; }
        public int SavedTaskbarState { get; set; }

        public Settings()
        {
            Theme = "Auto"; Opacity = 20; Blur = 1; IconSize = 44; DockAutoHide = 1;
            MenuBarReservesSpace = true; DockReservesSpace = false;
            ShowTrayIcons = true; ShowBatteryPercent = true;
            Fahrenheit = true; WeatherLocation = ""; Pinned = new List<PinnedApp>();
        }

        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacBar");
        static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath));
                    if (s != null)
                    {
                        if (s.Pinned == null) s.Pinned = new List<PinnedApp>();
                        s.Opacity = Math.Max(0, Math.Min(100, s.Opacity));
                        s.Blur = Math.Max(0, Math.Min(2, s.Blur));
                        s.IconSize = Math.Max(28, Math.Min(80, s.IconSize));
                        s.DockAutoHide = Math.Max(0, Math.Min(2, s.DockAutoHide));
                        if (s.SettingsVersion < 2)
                        {
                            // v2: slightly smaller default icons, dock auto-hides by default.
                            if (s.IconSize == 48) s.IconSize = 44;
                            s.DockAutoHide = 1;
                            s.SettingsVersion = 2;
                            s.Save();
                        }
                        return s;
                    }
                }
            }
            catch { }
            return new Settings { SettingsVersion = 2 };
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(this));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch { }
        }
    }
}
