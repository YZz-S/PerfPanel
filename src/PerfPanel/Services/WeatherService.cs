using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace PerfPanel.Services;

public sealed record WeatherInfo(double TempC, string Desc, double TMax, double TMin, string City);

/// <summary>Open-Meteo 免费天气(无需 key);位置可自定义,IP 自动定位结果缓存到 exe 旁 weather.json。</summary>
public sealed class WeatherService
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private string _configPath = "";

    /// <summary>下次刷新时间(UTC),UI 据此显示倒计时。</summary>
    public DateTime NextRefreshUtc { get; private set; } = DateTime.UtcNow + RefreshInterval;

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
            NextRefreshUtc = DateTime.UtcNow + RefreshInterval;
            return Current;
        }
        catch
        {
            return Current; // 失败时保留上次结果
        }
    }

    /// <summary>自定义城市:Open-Meteo 免费地理编码,写入 weather.json 并立即刷新。</summary>
    public async Task<(bool Ok, string Msg)> SetCityAsync(string name)
    {
        try
        {
            _configPath = Path.Combine(AppContext.BaseDirectory, "weather.json");
            var url = "https://geocoding-api.open-meteo.com/v1/search?name=" +
                      Uri.EscapeDataString(name) + "&count=1&language=zh";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                return (false, $"未找到城市:\"{name}\"\n请换一个名称试试(支持中文/拼音/英文)");

            var hit = results[0];
            double lat = hit.GetProperty("latitude").GetDouble();
            double lon = hit.GetProperty("longitude").GetDouble();
            string city = hit.GetProperty("name").GetString() ?? name;
            if (hit.TryGetProperty("admin1", out var admin1) && admin1.GetString() is { } a && a != city)
                city = $"{a}·{city}";

            await File.WriteAllTextAsync(_configPath,
                JsonSerializer.Serialize(new { lat, lon, city }));
            var w = await FetchAsync();
            return w != null
                ? (true, $"✓ 天气位置已设置:{city}\n({lat:F2}, {lon:F2})\n当前 {w.TempC:F0}° {w.Desc}")
                : (true, $"✓ 位置已保存:{city}\n(天气数据获取中,稍后自动显示)");
        }
        catch (Exception ex)
        {
            return (false, $"设置失败:{ex.Message}");
        }
    }

    private async Task<(double lat, double lon, string city)> ResolveLocationAsync()
    {
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
