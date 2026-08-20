using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace PerfPanel.Services;

public sealed record WeatherInfo(
    double TempC, string Desc, double TMax, double TMin, string City,
    int WindLevel, double WindSpeedKmh,
    double UvIndex, string UvLevel,
    int? Aqi, string AqiLevel);

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
                      "&current=temperature_2m,weather_code,wind_speed_10m" +
                      "&hourly=uv_index&forecast_hours=1" +
                      "&daily=temperature_2m_max,temperature_2m_min" +
                      "&timezone=auto&forecast_days=1";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            var root = doc.RootElement;
            var cur = root.GetProperty("current");
            double temp = cur.GetProperty("temperature_2m").GetDouble();
            int code = cur.GetProperty("weather_code").GetInt32();
            double windKmh = cur.GetProperty("wind_speed_10m").GetDouble();
            double uv = root.GetProperty("hourly").GetProperty("uv_index")[0].GetDouble();
            double tmax = root.GetProperty("daily").GetProperty("temperature_2m_max")[0].GetDouble();
            double tmin = root.GetProperty("daily").GetProperty("temperature_2m_min")[0].GetDouble();

            // 空气质量独立接口,失败仅隐藏该项
            (int? aqi, string aqiLevel) = await FetchAirQualityAsync(lat, lon);

            Current = new WeatherInfo(temp, WmoDescription(code), tmax, tmin, city,
                Beaufort(windKmh), windKmh, uv, UvLevel(uv), aqi, aqiLevel);
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

    /// <summary>空气质量(us_aqi):失败返回 null,面板隐藏该项。</summary>
    private async Task<(int? aqi, string level)> FetchAirQualityAsync(double lat, double lon)
    {
        try
        {
            var url = $"https://air-quality-api.open-meteo.com/v1/air-quality?latitude={lat:F4}&longitude={lon:F4}" +
                      "&current=us_aqi&timezone=auto";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            int aqi = (int)Math.Round(doc.RootElement.GetProperty("current").GetProperty("us_aqi").GetDouble());
            return (aqi, AqiLevel(aqi));
        }
        catch
        {
            return (null, "");
        }
    }

    /// <summary>蒲福风级(风速 km/h → 0~12 级)。</summary>
    private static int Beaufort(double kmh) => kmh switch
    {
        < 1 => 0, < 6 => 1, < 12 => 2, < 20 => 3, < 29 => 4, < 39 => 5,
        < 50 => 6, < 62 => 7, < 75 => 8, < 89 => 9, < 103 => 10, < 118 => 11, _ => 12,
    };

    private static string UvLevel(double uv) => uv switch
    {
        < 3 => "弱",
        < 6 => "中等",
        < 8 => "强",
        < 11 => "很强",
        _ => "极强",
    };

    private static string AqiLevel(int aqi) => aqi switch
    {
        <= 50 => "优",
        <= 100 => "良",
        <= 150 => "轻度污染",
        <= 200 => "中度污染",
        <= 300 => "重度污染",
        _ => "严重污染",
    };

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
