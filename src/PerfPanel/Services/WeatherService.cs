using System.IO;
using System.Net.Http;
using System.Text.Json;
using PerfPanel.Services;

namespace PerfPanel.Services;

public sealed record WeatherInfo(
    double TempC, string Desc, double TMax, double TMin, string City,
    int WindLevel, double WindSpeedKmh,
    double UvIndex, string UvLevel,
    int? Aqi, string AqiLevel,
    int? HumidityPct = null);

/// <summary>天气服务:双数据源。
/// 有高德 Key → 高德实况+预报(国内稳定,地址级定位),UV/AQI 用 Open-Meteo 增强;
/// 无 Key → Open-Meteo 免费链(IP 定位 ipapi→ipwho.is→反查中文名)。</summary>
public sealed class WeatherService
{
    private const double FallbackLat = 39.9042, FallbackLon = 116.4074; // 全部失败时的兜底:北京
    private const string FallbackAdcode = "110000";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly string _configPath = Path.Combine(AppContext.BaseDirectory, "weather.json");

    /// <summary>下次刷新时间(UTC),UI 据此显示倒计时。</summary>
    public DateTime NextRefreshUtc { get; private set; } = DateTime.UtcNow + RefreshInterval;

    public WeatherInfo? Current { get; private set; }
    public bool Available => Current != null;

    public static TimeSpan RefreshInterval => TimeSpan.FromMinutes(Math.Clamp(Config.Current.WeatherRefreshMinutes, 10, 120));

    private static bool HasAmap => !string.IsNullOrWhiteSpace(Config.Current.AmapKey);

    // ==================== 主入口 ====================

    public async Task<WeatherInfo?> FetchAsync()
    {
        WeatherInfo? fresh = null;
        try
        {
            fresh = HasAmap ? await FetchAmapAsync() : await FetchOpenMeteoAsync();
        }
        catch { }
        if (fresh != null)
        {
            Current = fresh;
            NextRefreshUtc = DateTime.UtcNow + RefreshInterval;
        }
        return Current;
    }

    /// <summary>设置位置:高德地理编码优先(地址级精准),否则 Photon/Open-Meteo 链。</summary>
    public async Task<(bool Ok, string Msg)> SetCityAsync(string name)
    {
        if (HasAmap)
        {
            var g = await AmapGeocodeAsync(name);
            if (g != null)
            {
                Config.Current.City = g.City;
                Config.Save();
                var w = await FetchAsync();
                return w != null
                    ? (true, $"✓ 天气位置已设置:{g.City}\n当前 {w.TempC:F0}° {w.Desc}")
                    : (true, $"✓ 位置已保存:{g.City}\n(天气数据获取中)");
            }
            return (false, $"高德未能解析该地址:\"{name}\"\n请检查 Key 是否有效,或换一个写法");
        }

        var hit = await PhotonSearchAsync(name)                 // ① 地址/城市名(Photon,国内较稳)
               ?? await GeocodeCityAsync(name)                  // ② Open-Meteo 城市名
               ?? await PhotonVariantsAsync(name)               // ③ 左剥离:…区垡头街道 → 垡头街道
               ?? await TruncatedSearchAsync(name);             // ④ 右截断:…街道 → …区 → …市
        if (hit == null)
        {
            return (false, $"未找到位置:\"{name}\"\n\n支持城市名(北京、成都)或区/街道名(朝阳区、垡头街道)\n" +
                            "建议在设置中填写高德 Key,可精确解析详细地址");
        }

        await WriteCacheAsync(hit.Lat, hit.Lon, hit.City, null);
        Config.Current.City = hit.City;
        Config.Save();
        var wf = await FetchAsync();
        return wf != null
            ? (true, $"✓ 天气位置已设置:{hit.City}\n({hit.Lat:F2}, {hit.Lon:F2})\n当前 {wf.TempC:F0}° {wf.Desc}")
            : (true, $"✓ 位置已保存:{hit.City}\n(天气数据获取中,稍后自动显示)");
    }

    /// <summary>恢复自动定位:清除手动城市与坐标缓存,立即重新定位。</summary>
    public async Task ResetToAutoAsync()
    {
        Config.Current.City = "";
        Config.Save();
        try { File.Delete(_configPath); } catch { }
        await FetchAsync();
    }

    /// <summary>设置窗口保存 Key 时校验连通性。</summary>
    public async Task<(bool Ok, string Msg)> TestAmapKeyAsync(string key)
    {
        try
        {
            var url = $"https://restapi.amap.com/v3/weather/weatherInfo?city={FallbackAdcode}&key={Uri.EscapeDataString(key)}&extensions=base";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            var root = doc.RootElement;
            if (root.GetProperty("status").GetString() == "1")
                return (true, "✓ Key 有效,已启用高德数据源");
            string why = root.TryGetProperty("info", out var info) ? info.GetString() ?? "" : "";
            return (false, $"✗ Key 校验失败:{(why.Length > 0 ? why : "未知错误")}");
        }
        catch (Exception ex)
        {
            return (false, $"✗ 请求失败:{ex.Message}");
        }
    }

    // ==================== 高德数据源 ====================

    private async Task<WeatherInfo?> FetchAmapAsync()
    {
        string key = Config.Current.AmapKey.Trim();
        var (lat, lon, city, adcode) = await AmapResolveAsync(key);

        var baseJson = await _http.GetStringAsync(
            $"https://restapi.amap.com/v3/weather/weatherInfo?city={adcode}&key={key}&extensions=base");
        using (var doc = JsonDocument.Parse(baseJson))
        {
            var root = doc.RootElement;
            if (root.GetProperty("status").GetString() != "1") return null;
            var lives = root.GetProperty("lives");
            if (lives.GetArrayLength() == 0) return null;
            var l = lives[0];

            double temp = ParseD(l, "temperature");
            string desc = Str(l, "weather");
            int? humidity = ParseN(l, "humidity");
            int windLevel = ParseWindLevel(l);
            // 预报取今日高低温
            double tmax = temp, tmin = temp;
            try
            {
                var allJson = await _http.GetStringAsync(
                    $"https://restapi.amap.com/v3/weather/weatherInfo?city={adcode}&key={key}&extensions=all");
                using var all = JsonDocument.Parse(allJson);
                var fc = all.RootElement.GetProperty("forecasts")[0]
                              .GetProperty("casts")[0];
                tmax = ParseD(fc, "daytemp");
                tmin = ParseD(fc, "nighttemp");
            }
            catch { }

            // UV / AQI 增强(Open-Meteo,失败隐藏)
            var (uv, uvLevel, aqi, aqiLevel) = await FetchEnhancementsAsync(lat, lon);

            string label = string.IsNullOrEmpty(city) ? Str(l, "city") : city;
            return new WeatherInfo(temp, desc, tmax, tmin, label,
                windLevel, BeaufortMidKmh(windLevel), uv, uvLevel, aqi, aqiLevel, humidity);
        }
    }

    /// <summary>高德定位:缓存(含 adcode)→ 手动城市地理编码 → 高德 IP 定位 → 北京兜底。</summary>
    private async Task<(double lat, double lon, string city, string adcode)> AmapResolveAsync(string key)
    {
        string manual = Config.Current.City.Trim();

        if (TryReadCache(out var cLat, out var cLon, out var cCity, out var cAdcode) &&
            !string.IsNullOrEmpty(cAdcode))
        {
            string label = manual.Length > 0 ? manual : cCity;
            return (cLat, cLon, label, cAdcode);
        }

        if (manual.Length > 0)
        {
            var g = await AmapGeocodeAsync(manual);
            if (g != null) return (g.Lat, g.Lon, g.City, g.Adcode ?? "");
        }

        try
        {
            var ipJson = await _http.GetStringAsync(
                $"https://restapi.amap.com/v3/ip?key={Uri.EscapeDataString(key)}");
            using var doc = JsonDocument.Parse(ipJson);
            var root = doc.RootElement;
            if (root.GetProperty("status").GetString() == "1")
            {
                string adcode = Str(root, "adcode");
                string city = Str(root, "city");
                if (adcode.Length > 0)
                {
                    var (clat, clon) = ParseRectangleCenter(Str(root, "rectangle"));
                    await WriteCacheAsync(clat, clon, city, adcode);
                    return (clat, clon, city, adcode);
                }
            }
        }
        catch { }

        return (FallbackLat, FallbackLon, "北京", FallbackAdcode);
    }

    /// <summary>高德地理编码:支持"北京市朝阳区垡头街道"级别的详细地址。</summary>
    private async Task<GeoHit?> AmapGeocodeAsync(string address)
    {
        try
        {
            string key = Config.Current.AmapKey.Trim();
            var url = "https://restapi.amap.com/v3/geocode/geo?address=" +
                      Uri.EscapeDataString(address) + "&key=" + Uri.EscapeDataString(key);
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            var root = doc.RootElement;
            if (root.GetProperty("status").GetString() != "1") return null;
            if (!root.TryGetProperty("geocodes", out var geos) || geos.GetArrayLength() == 0) return null;

            var g = geos[0];
            string district = Str(g, "district");
            string city = Str(g, "city");
            string province = Str(g, "province");
            string label = district.Length > 0 ? city + district
                        : city.Length > 0 ? city : province;
            if (label.Length == 0) label = address;

            string adcode = Str(g, "adcode");
            (double lon, double lat) = ParseLocation(Str(g, "location"));
            await WriteCacheAsync(lat, lon, label, adcode);
            return new GeoHit(lat, lon, label, adcode);
        }
        catch
        {
            return null;
        }
    }

    // ==================== Open-Meteo 数据源(无 Key 免费链) ====================

    private async Task<WeatherInfo?> FetchOpenMeteoAsync()
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

        var (aqi, aqiLevel) = await FetchAirQualityAsync(lat, lon);
        return new WeatherInfo(temp, WmoDescription(code), tmax, tmin, city,
            Beaufort(windKmh), windKmh, uv, UvLevel(uv), aqi, aqiLevel);
    }

    // ---------- OM 定位链:手动城市 → 缓存 → IP(ipapi→ipwho.is)→ 反查中文 ----------

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
                        var zhName = await ReverseGeocodeAsync(lat, lon);
                        if (!string.IsNullOrEmpty(zhName))
                        {
                            city = zhName;
                            await WriteCacheAsync(lat, lon, city, null);
                        }
                    }
                    return (lat, lon, city);
                }
            }
            catch { }
        }

        var (lat2, lon2, city2) = await IpLocateAsync();
        var zh = await ReverseGeocodeAsync(lat2, lon2);
        if (!string.IsNullOrEmpty(zh)) city2 = zh;
        if (string.IsNullOrEmpty(city2))
            city2 = IsFallback(lat2, lon2) ? "北京" : "";

        if (!string.IsNullOrEmpty(city2))
            await WriteCacheAsync(lat2, lon2, city2, null);
        return (lat2, lon2, city2);
    }

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

    // ---------- OM 地理编码兜底链(无 Key 时) ----------

    private sealed record GeoHit(double Lat, double Lon, string City, string? Adcode = null);

    private async Task<GeoHit?> GeocodeCityAsync(string name)
    {
        try
        {
            var url = "https://geocoding-api.open-meteo.com/v1/search?name=" +
                      Uri.EscapeDataString(name) + "&count=1&language=zh";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                return null;

            var hit = results[0];
            double lat = hit.GetProperty("latitude").GetDouble();
            double lon = hit.GetProperty("longitude").GetDouble();
            string city = hit.GetProperty("name").GetString() ?? name;
            if (hit.TryGetProperty("admin1", out var admin1) && admin1.GetString() is { } a && a != city)
                city = $"{a}·{city}";
            return new GeoHit(lat, lon, city);
        }
        catch
        {
            return null;
        }
    }

    private async Task<GeoHit?> PhotonSearchAsync(string query)
    {
        try
        {
            var url = "https://photon.komoot.io/api/?limit=5&lang=default&q=" + Uri.EscapeDataString(query);
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("features", out var feats)) return null;

            foreach (var f in feats.EnumerateArray())
            {
                if (!f.TryGetProperty("properties", out var p)) continue;
                if (!p.TryGetProperty("osm_key", out var key)) continue;
                var k = key.GetString();
                if (k != "place" && k != "boundary") continue; // 排除 POI 误匹配

                string label = Str(p, "name");
                if (label.Length == 0) continue;
                string parent = FirstNonEmpty(p, "district", "city", "state");
                if (parent.Length > 0 && parent != label) label = $"{label}·{parent}";

                double lon = f.GetProperty("geometry").GetProperty("coordinates")[0].GetDouble();
                double lat = f.GetProperty("geometry").GetProperty("coordinates")[1].GetDouble();
                return new GeoHit(lat, lon, label);
            }
        }
        catch { }
        return null;
    }

    private async Task<GeoHit?> PhotonVariantsAsync(string name)
    {
        foreach (var variant in AddressVariants(name).Skip(1))
        {
            var hit = await PhotonSearchAsync(variant);
            if (hit != null) return hit;
        }
        return null;
    }

    private static IEnumerable<string> AddressVariants(string name)
    {
        yield return name;
        var cur = name;
        while (true)
        {
            int idx = cur.IndexOfAny(new[] { '市', '区', '县', '省' });
            if (idx < 0 || idx + 1 >= cur.Length) break;
            cur = cur[(idx + 1)..];
            yield return cur;
        }
    }

    private async Task<GeoHit?> TruncatedSearchAsync(string name)
    {
        var prefixes = new List<string>();
        for (int i = 0; i < name.Length - 1; i++)
        {
            if (name[i] is '市' or '区' or '县')
                prefixes.Add(name[..(i + 1)]);
        }
        foreach (var prefix in prefixes.OrderByDescending(x => x.Length))
        {
            var hit = await PhotonSearchAsync(prefix) ?? await GeocodeCityAsync(prefix);
            if (hit != null) return hit;
        }
        return null;
    }

    // ==================== 增强项(UV / AQI,均容错) ====================

    private async Task<(double uv, string uvLevel, int? aqi, string aqiLevel)> FetchEnhancementsAsync(double lat, double lon)
    {
        double uv = 0;
        string uvLevel = "—";
        try
        {
            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat:F4}&longitude={lon:F4}" +
                      "&hourly=uv_index&forecast_hours=1&timezone=auto";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            uv = doc.RootElement.GetProperty("hourly").GetProperty("uv_index")[0].GetDouble();
            uvLevel = UvLevel(uv);
        }
        catch { }

        var (aqi, aqiLevel) = await FetchAirQualityAsync(lat, lon);
        return (uv, uvLevel, aqi, aqiLevel);
    }

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

    // ==================== 缓存与工具 ====================

    private async Task WriteCacheAsync(double lat, double lon, string city, string? adcode)
    {
        try
        {
            await File.WriteAllTextAsync(_configPath,
                JsonSerializer.Serialize(new { lat, lon, city, adcode }));
        }
        catch { }
    }

    private bool TryReadCache(out double lat, out double lon, out string city, out string adcode)
    {
        (lat, lon, city, adcode) = (0, 0, "", "");
        try
        {
            if (!File.Exists(_configPath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(_configPath));
            var r = doc.RootElement;
            lat = r.GetProperty("lat").GetDouble();
            lon = r.GetProperty("lon").GetDouble();
            city = r.GetProperty("city").GetString() ?? "";
            adcode = r.TryGetProperty("adcode", out var a) ? a.GetString() ?? "" : "";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Str(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }

    private static string FirstNonEmpty(JsonElement obj, params string[] props)
    {
        foreach (var p in props)
        {
            var s = Str(obj, p);
            if (s.Length > 0) return s;
        }
        return "";
    }

    private static double ParseD(JsonElement obj, string prop) =>
        double.TryParse(Str(obj, prop), out var v) ? v : 0;

    private static int? ParseN(JsonElement obj, string prop) =>
        int.TryParse(Str(obj, prop), out var v) ? v : null;

    /// <summary>高德 windpower 形如 "3" 或 "≤3",取数字风级。</summary>
    private static int ParseWindLevel(JsonElement obj)
    {
        var s = Str(obj, "windpower");
        int start = 0;
        while (start < s.Length && !char.IsDigit(s[start])) start++;
        return int.TryParse(s.AsSpan(start), out var lv) ? lv : 0;
    }

    /// <summary>风级 → 区间代表风速(km/h)。</summary>
    private static double BeaufortMidKmh(int level) => level switch
    {
        0 => 0.5, 1 => 3, 2 => 8, 3 => 15, 4 => 24, 5 => 33,
        6 => 44, 7 => 55, 8 => 68, 9 => 81, 10 => 95, 11 => 110, _ => 125,
    };

    private static (double lon, double lat) ParseLocation(string s)
    {
        var parts = s.Split(',');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], out var lon) && double.TryParse(parts[1], out var lat))
            return (lon, lat);
        return (FallbackLon, FallbackLat);
    }

    private static (double lat, double lon) ParseRectangleCenter(string s)
    {
        // "116.02,39.78,116.62,40.06" → 中心点
        var parts = s.Split(',');
        if (parts.Length == 4 &&
            double.TryParse(parts[0], out var x1) && double.TryParse(parts[1], out var y1) &&
            double.TryParse(parts[2], out var x2) && double.TryParse(parts[3], out var y2))
            return ((y1 + y2) / 2, (x1 + x2) / 2);
        return (FallbackLat, FallbackLon);
    }

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
