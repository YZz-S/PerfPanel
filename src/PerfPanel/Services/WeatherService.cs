using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace PerfPanel.Services;

public sealed record WeatherInfo(double TempC, string Desc, double TMax, double TMin, string City);

/// <summary>Open-Meteo 免费天气(无需 key);定位用 ipapi.co,结果缓存到 exe 旁 weather.json。</summary>
public sealed class WeatherService
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private string _configPath = "";

    public WeatherInfo? Current { get; private set; }
    public bool Available => Current != null;

    public async Task<WeatherInfo?> FetchAsync()
    {
        try
        {
            _configPath = Path.Combine(AppContext.BaseDirectory, "weather.json");
            (double lat, double lon, string city) = await ResolveLocationAsync();

            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat:F4}&longitude={lon:F4}" +
                      "&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min" +
                      "&timezone=auto&forecast_days=1";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            var root = doc.RootElement;
            double temp = root.GetProperty("current").GetProperty("temperature_2m").GetDouble();
            int code = root.GetProperty("current").GetProperty("weather_code").GetInt32();
            double tmax = root.GetProperty("daily").GetProperty("temperature_2m_max")[0].GetDouble();
            double tmin = root.GetProperty("daily").GetProperty("temperature_2m_min")[0].GetDouble();

            Current = new WeatherInfo(temp, WmoDescription(code), tmax, tmin, city);
            return Current;
        }
        catch
        {
            return Current; // 失败时保留上次结果
        }
    }

    private async Task<(double lat, double lon, string city)> ResolveLocationAsync()
    {
        // 已有本地配置则直接用
        if (File.Exists(_configPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_configPath));
                var r = doc.RootElement;
                return (r.GetProperty("lat").GetDouble(), r.GetProperty("lon").GetDouble(),
                        r.GetProperty("city").GetString() ?? "");
            }
            catch { }
        }

        double lat = 39.9042, lon = 116.4074; // 兜底:北京
        string city = "";
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync("https://ipapi.co/json/"));
            var r = doc.RootElement;
            if (r.TryGetProperty("latitude", out var la)) lat = la.GetDouble();
            if (r.TryGetProperty("longitude", out var lo)) lon = lo.GetDouble();
            if (r.TryGetProperty("city", out var c)) city = c.GetString() ?? "";
        }
        catch { }

        try
        {
            await File.WriteAllTextAsync(_configPath,
                JsonSerializer.Serialize(new { lat, lon, city }));
        }
        catch { }
        return (lat, lon, city);
    }

    private static string WmoDescription(int code) => code switch
    {
        0 => "晴",
        1 or 2 => "多云",
        3 => "阴",
        45 or 48 => "雾",
        >= 51 and <= 57 => "毛毛雨",
        >= 61 and <= 67 => "雨",
        >= 71 and <= 77 => "雪",
        >= 80 and <= 82 => "阵雨",
        85 or 86 => "阵雪",
        >= 95 => "雷雨",
        _ => "—",
    };
}
