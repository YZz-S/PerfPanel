using System.IO;
using System.Net.Http;
using System.Text.Json;
using PerfPanel.Services;

namespace PerfPanel.Services;

public sealed record WeatherInfo(
    double TempC, string Desc, double TMax, double TMin, string City,
    int WindLevel, double WindSpeedKmh,
    double UvIndex, string UvLevel,
    int? Aqi, string AqiLevel);

/// <summary>Open-Meteo 免费天气(无需 key)。定位链:手动城市 → 缓存 → IP(ipapi→ipwho.is 备用)→ 坐标反查城市名。</summary>
public sealed class WeatherService
{
    private const double FallbackLat = 39.9042, FallbackLon = 116.4074; // 全部失败时的兜底:北京

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly string _configPath = Path.Combine(AppContext.BaseDirectory, "weather.json");

    /// <summary>下次刷新时间(UTC),UI 据此显示倒计时。</summary>
    public DateTime NextRefreshUtc { get; private set; } = DateTime.UtcNow + RefreshInterval;

    public WeatherInfo? Current { get; private set; }
    public bool Available => Current != null;

    public static TimeSpan RefreshInterval => TimeSpan.FromMinutes(Math.Clamp(Config.Current.WeatherRefreshMinutes, 10, 120));

    public async Task<WeatherInfo?> FetchAsync()
    {
        try
        {
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

    /// <summary>自定义城市:Open-Meteo 免费地理编码,写 weather.json + config.City 并立即刷新。</summary>
    public async Task<(bool Ok, string Msg)> SetCityAsync(string name)
    {
        try
        {
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
            Config.Current.City = city;
            Config.Save();
            var w = await FetchAsync();
            return w != null
                ? (true, $"✓ 天气位置已设置:{city}\n当前 {w.TempC:F0}° {w.Desc}")
                : (true, $"✓ 位置已保存:{city}\n(天气数据获取中,稍后自动显示)");
        }
        catch (Exception ex)
        {
            return (false, $"设置失败:{ex.Message}");
        }
    }

    /// <summary>恢复自动定位:清除手动城市与坐标缓存,立即重新定位。</summary>
    public async Task ResetToAutoAsync()
    {
        Config.Current.City = "";
        Config.Save();
        try { File.Delete(_configPath); } catch { }
        await FetchAsync();
    }

    // ---------- 定位链 ----------

    private async Task<(double lat, double lon, string city)> ResolveLocationAsync()
    {
        string manual = Config.Current.City.Trim();

        if (File.Exists(_configPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_configPath));
                var r = doc.RootElement;
                double lat = r.GetProperty("lat").GetDouble();
                double lon = r.GetProperty("lon").GetDouble();
                string city = r.GetProperty("city").GetString() ?? "";

                if (!string.IsNullOrEmpty(manual))
                    return (lat, lon, manual);

                // 城市为空 = 坐标可能来自失败兜底,不可信,重新 IP 定位
                if (!string.IsNullOrEmpty(city))
                {
                    // 纯 ASCII 城市名(如 "Beijing")反查中文名并回写缓存
                    if (city.All(c => c < 128))
                    {
                        var zh = await ReverseGeocodeAsync(lat, lon);
                        if (!string.IsNullOrEmpty(zh))
                        {
                            city = zh;
                            try
                            {
                                await File.WriteAllTextAsync(_configPath,
                                    JsonSerializer.Serialize(new { lat, lon, city }));
                            }
                            catch { }
                        }
                    }
                    return (lat, lon, city);
                }
            }
            catch { }
        }

        var (lat2, lon2, city2) = await IpLocateAsync();
        // 反查中文名:IP 服务常返回英文(如 "Beijing"),反查统一本地化
        var zhName = await ReverseGeocodeAsync(lat2, lon2);
        if (!string.IsNullOrEmpty(zhName)) city2 = zhName;
        if (string.IsNullOrEmpty(city2))
            city2 = IsFallback(lat2, lon2) ? "北京" : ""; // 兜底坐标标明,避免"本地"

        if (!string.IsNullOrEmpty(city2))
        {
            try
            {
                await File.WriteAllTextAsync(_configPath,
                    JsonSerializer.Serialize(new { lat = lat2, lon = lon2, city = city2 }));
            }
            catch { }
        }
        return (lat2, lon2, city2);
    }

    /// <summary>IP 定位:ipapi.co → ipwho.is 备用 → 北京兜底。</summary>
    private async Task<(double lat, double lon, string city)> IpLocateAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync("https://ipapi.co/json/"));
            var r = doc.RootElement;
            if (r.TryGetProperty("latitude", out var la) && r.TryGetProperty("longitude", out var lo))
            {
                string city = r.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
                return (la.GetDouble(), lo.GetDouble(), city);
            }
        }
        catch { }

        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync("https://ipwho.is/"));
            var r = doc.RootElement;
            if (r.TryGetProperty("latitude", out var la) && r.TryGetProperty("longitude", out var lo))
            {
                string city = r.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
                return (la.GetDouble(), lo.GetDouble(), city);
            }
        }
        catch { }

        return (FallbackLat, FallbackLon, "");
    }

    /// <summary>坐标反查城市名(BigDataCloud 免费),中文返回。</summary>
    private async Task<string> ReverseGeocodeAsync(double lat, double lon)
    {
        try
        {
            var url = $"https://api.bigdatacloud.net/data/reverse-geocode-client?latitude={lat:F4}&longitude={lon:F4}&localityLanguage=zh";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            var r = doc.RootElement;
            if (r.TryGetProperty("city", out var c) && c.GetString() is { Length: > 0 } city)
                return city;
            if (r.TryGetProperty("locality", out var l) && l.GetString() is { Length: > 0 } loc)
                return loc;
        }
        catch { }
        return "";
    }

    private static bool IsFallback(double lat, double lon) =>
        Math.Abs(lat - FallbackLat) < 0.001 && Math.Abs(lon - FallbackLon) < 0.001;

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
