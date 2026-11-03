using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Threading;

namespace MacBar
{
    public class WeatherDay
    {
        public string Date { get; set; }  // yyyy-MM-dd
        public int Code { get; set; }
        public double MaxC { get; set; }
        public double MinC { get; set; }
    }

    public class WeatherData
    {
        public double TempC { get; set; }
        public double FeelsC { get; set; }
        public double Humidity { get; set; }
        public double WindKmh { get; set; }
        public int Code { get; set; }
        public bool IsDay { get; set; }
        public DateTime FetchedUtc { get; set; }
        public string Location { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public List<WeatherDay> Days { get; set; }
    }

    public class GeoResult
    {
        public string Label; public double Lat, Lon;
        public override string ToString() { return Label; }
    }

    /// <summary>Weather from Open-Meteo (free, no API key). Data is stored in Celsius and converted for display.</summary>
    public class WeatherService
    {
        public WeatherData Current { get; private set; }
        public bool LastFetchFailed { get; private set; }
        public event Action Updated;
        readonly Settings settings;
        readonly DispatcherTimer timer;
        static string CachePath { get { return Path.Combine(Settings.Dir, "weather.json"); } }

        static WeatherService()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        public WeatherService(Settings s)
        {
            settings = s;
            LoadCache();
            timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            timer.Tick += delegate { Refresh(); };
            timer.Start();
        }

        void LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath)) return;
                var d = new JavaScriptSerializer().Deserialize<WeatherData>(File.ReadAllText(CachePath));
                if (d != null && settings.HasLocation && Math.Abs(d.Lat - settings.Latitude) < 0.01 && Math.Abs(d.Lon - settings.Longitude) < 0.01)
                    Current = d;
            }
            catch { }
        }

        public void LocationChanged()
        {
            Current = null;
            try { File.Delete(CachePath); } catch { }
            if (Updated != null) Updated();
            Refresh();
        }

        static WebClient Client()
        {
            var wc = new WebClient { Encoding = Encoding.UTF8 };
            wc.Headers[HttpRequestHeader.UserAgent] = "MacBar/1.0";
            return wc;
        }

        public async void Refresh()
        {
            if (!settings.HasLocation) return;
            double lat = settings.Latitude, lon = settings.Longitude;
            string url = string.Format(CultureInfo.InvariantCulture,
                "https://api.open-meteo.com/v1/forecast?latitude={0:0.####}&longitude={1:0.####}" +
                "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,is_day,wind_speed_10m" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=6", lat, lon);
            try
            {
                string json;
                using (var wc = Client()) json = await wc.DownloadStringTaskAsync(url);
                var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
                var cur = (Dictionary<string, object>)root["current"];
                var daily = (Dictionary<string, object>)root["daily"];
                var d = new WeatherData
                {
                    TempC = Num(cur["temperature_2m"]), FeelsC = Num(cur["apparent_temperature"]),
                    Humidity = Num(cur["relative_humidity_2m"]), WindKmh = Num(cur["wind_speed_10m"]),
                    Code = (int)Num(cur["weather_code"]), IsDay = Num(cur["is_day"]) > 0,
                    FetchedUtc = DateTime.UtcNow, Location = settings.WeatherLocation, Lat = lat, Lon = lon,
                    Days = new List<WeatherDay>()
                };
                var times = (object[])daily["time"]; var codes = (object[])daily["weather_code"];
                var max = (object[])daily["temperature_2m_max"]; var min = (object[])daily["temperature_2m_min"];
                for (int i = 0; i < times.Length; i++)
                    d.Days.Add(new WeatherDay { Date = (string)times[i], Code = (int)Num(codes[i]), MaxC = Num(max[i]), MinC = Num(min[i]) });
                // Ignore late results for a location the user has since changed.
                if (!settings.HasLocation || lat != settings.Latitude || lon != settings.Longitude) return;
                Current = d;
                LastFetchFailed = false;
                try { Directory.CreateDirectory(Settings.Dir); File.WriteAllText(CachePath, new JavaScriptSerializer().Serialize(d)); } catch { }
            }
            catch { LastFetchFailed = true; }
            if (Updated != null) Updated();
        }

        static double Num(object o) { return o == null ? 0 : Convert.ToDouble(o, CultureInfo.InvariantCulture); }

        public static async Task<List<GeoResult>> Search(string query)
        {
            var results = new List<GeoResult>();
            string url = "https://geocoding-api.open-meteo.com/v1/search?count=8&language=en&format=json&name=" + Uri.EscapeDataString(query.Trim());
            string json;
            using (var wc = Client()) json = await wc.DownloadStringTaskAsync(url);
            var root = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            object arr;
            if (!root.TryGetValue("results", out arr) || arr == null) return results;
            foreach (Dictionary<string, object> r in (object[])arr)
            {
                var parts = new List<string> { (string)r["name"] };
                object a1, c;
                if (r.TryGetValue("admin1", out a1) && a1 != null) parts.Add((string)a1);
                if (r.TryGetValue("country", out c) && c != null) parts.Add((string)c);
                results.Add(new GeoResult { Label = string.Join(", ", parts), Lat = Num(r["latitude"]), Lon = Num(r["longitude"]) });
            }
            return results;
        }

        public string Temp(double c)
        {
            double v = settings.Fahrenheit ? c * 9 / 5 + 32 : c;
            return Math.Round(v).ToString("0", CultureInfo.CurrentCulture) + "°";
        }

        public string Wind(double kmh)
        {
            return settings.Fahrenheit ? Math.Round(kmh * 0.621371) + " mph" : Math.Round(kmh) + " km/h";
        }

        // WMO weather interpretation codes
        public static string Describe(int code)
        {
            if (code == 0) return "Clear";
            if (code == 1) return "Mostly Clear";
            if (code == 2) return "Partly Cloudy";
            if (code == 3) return "Cloudy";
            if (code == 45 || code == 48) return "Fog";
            if (code >= 51 && code <= 57) return "Drizzle";
            if (code >= 61 && code <= 67) return "Rain";
            if (code >= 71 && code <= 77) return "Snow";
            if (code >= 80 && code <= 82) return "Showers";
            if (code == 85 || code == 86) return "Snow Showers";
            if (code >= 95) return "Thunderstorms";
            return "—";
        }

        public static string Glyph(int code, bool day)
        {
            if (code <= 1) return day ? "☀" : "☾";            // ☀ ☾
            if (code == 2) return day ? "⛅" : "☁";             // ⛅ ☁
            if (code == 3) return "☁";                                 // ☁
            if (code == 45 || code == 48) return "\U0001F32B";              // 🌫
            if (code >= 51 && code <= 67) return "\U0001F327";              // 🌧
            if (code >= 71 && code <= 77) return "❄";                  // ❄
            if (code >= 80 && code <= 82) return day ? "\U0001F326" : "\U0001F327";
            if (code == 85 || code == 86) return "\U0001F328";              // 🌨
            if (code >= 95) return "⛈";                                // ⛈
            return "☁";
        }
    }
}
