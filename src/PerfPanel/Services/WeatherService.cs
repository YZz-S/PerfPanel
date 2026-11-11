using System.IO;
using System.Net.Http;
using System.Text.Json;
using PerfPanel.Services;

namespace PerfPanel.Services;

/// <summary>风级以原始文本承载(支持高德「≤3」),风速只在 Open-Meteo 提供真实值时才有数(高德不编 km/h)。</summary>
public sealed record WeatherInfo(
    double TempC, string Desc, double TMax, double TMin, string City,
    string WindLevelTxt, string WindDir, double? WindSpeedKmh,
    double UvIndex, string UvLevel,
    int? Aqi, string AqiLevel,
    int? HumidityPct = null)
{
    public int WindLevel =>
        int.TryParse(WindLevelTxt.Trim().TrimStart('≤', '<'), out var v) ? v : 0;

    /// <summary>风行文案:「≤3级·南风」,空段跳过。</summary>
    public string WindLabel
    {
        get
        {
            string lvl = WindLevelTxt.Trim();
            if (lvl.Length > 0 && !lvl.EndsWith("级") && char.IsDigit(lvl[^1])) lvl += "级";
            if (lvl.Length == 0 && WindLevel > 0) lvl = WindLevel + "级";
            string dir = WindDir.Trim();
            if (dir.Length > 0 && !dir.EndsWith("风")) dir += "风";
            string main = lvl.Length > 0 && dir.Length > 0 ? $"{lvl}·{dir}" : lvl + dir;
            return main.Length > 0 ? main : "--";
        }
    }
}

/// <summary>天气服务:三数据源。
/// 填了和风天气(专属 API Host + API KEY) → 和风为主源:温度/天气现象/风级风向/湿度/UV/AQI 全国内口径,
/// 高德只承担定位/地理编码;和风失败或未配置 → 有高德 Key 走高德实况+预报(UV/AQI/风速 Open-Meteo 增强);
/// 都没有 → Open-Meteo 免费链(IP 定位 ipapi→ipwho.is→反查中文名)。增强项逐项容错回落。</summary>
public sealed class WeatherService
{
    private const double FallbackLat = 39.9042, FallbackLon = 116.4074; // 全部失败时的兜底:北京
    private const string FallbackAdcode = "110000";

    // 和风服务端强制 gzip,必须开自动解压,否则读回乱码、JSON 解析全挂(Open-Meteo/高德不受影响)
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
    })
    { Timeout = TimeSpan.FromSeconds(8) };
    private readonly string _configPath = Path.Combine(AppContext.BaseDirectory, "weather.json");

    /// <summary>下次刷新时间(UTC),UI 据此显示倒计时。</summary>
    public DateTime NextRefreshUtc { get; private set; } = DateTime.UtcNow + RefreshInterval;

    public WeatherInfo? Current { get; private set; }
    public bool Available => Current != null;

    public static TimeSpan RefreshInterval => TimeSpan.FromMinutes(Math.Clamp(Config.Current.WeatherRefreshMinutes, 10, 120));

    private static bool HasAmap => !string.IsNullOrWhiteSpace(Config.Current.AmapKey);
    private static bool HasQWeather =>
        !string.IsNullOrWhiteSpace(Config.Current.QWeatherHost) &&
        !string.IsNullOrWhiteSpace(Config.Current.QWeatherKey);

    // ==================== 主入口 ====================

    public async Task<WeatherInfo?> FetchAsync()
    {
        WeatherInfo? fresh = null;
        try
        {
            fresh = (HasQWeather ? await FetchQWeatherMainAsync() : null)
                 ?? (HasAmap ? await FetchAmapAsync() : await FetchOpenMeteoAsync());
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
            var g = await AmapGeocodeAsync(NormalizeManualAddress(name));
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

    /// <summary>设置窗口保存和风配置时校验:先探实时天气(免费订阅必含)验 Key/Host,
    /// 再探空气质量;免费订阅若无该 API 只提示降级,配置仍可保存(运行时逐项回落 Open-Meteo)。</summary>
    public async Task<(bool Ok, string Msg)> TestQWeatherAsync(string host, string key)
    {
        try
        {
            if (key.Length == 0)
                return (false, "✗ API Key 为空;API Host 与 Key 都需要填写");
            host = host.Trim().TrimEnd('/');
            if (host.Length == 0 || !host.Contains('.'))
                return (false, "✗ API Host 无效:形如 abcxyz.qweatherapi.com(控制台首页复制)");

            if (await QwGetAsync(key, host, "/weather/v1/current/39.90/116.41") is null)
                return (false, "✗ Key/Host 校验失败:请确认凭据的身份认证方式是「API KEY」" +
                               "(选成 JSON Web Token 则此 Key 无效)、Host 是否控制台首页复制");

            return await QwGetAsync(key, host, "/airquality/v1/current/39.90/116.41") is { } aq &&
                   QwCnAqi(aq) is { } aqi
                ? (true, $"✓ 和风天气已启用\n北京测试点 AQI(中国标准) {aqi} {AqiLevel(aqi)}")
                : (true, "✓ Key 有效:紫外线/风速走和风;空气质量取不到时自动回落 Open-Meteo");
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
            string windRaw = Str(l, "windpower");        // 「3」/「≤3」原样展示,不取区间中值
            string windDir = Str(l, "winddirection");
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

            // UV / AQI / 真实风速增强(Open-Meteo,失败降级:UV与AQI隐藏,风速缺省致 UI 不显示 km/h)
            var (uv, uvLevel, aqi, aqiLevel, omWind) = await FetchEnhancementsAsync(lat, lon);

            string label = string.IsNullOrEmpty(city) ? Str(l, "city") : city;
            return new WeatherInfo(temp, desc, tmax, tmin, label,
                windRaw, windDir, omWind, uv, uvLevel, aqi, aqiLevel, humidity);
        }
    }

    /// <summary>高德定位:缓存(含 adcode)→ 手动城市地理编码 → 高德 IP 定位 → 北京兜底。</summary>
    private async Task<(double lat, double lon, string city, string adcode)> AmapResolveAsync(string key)
    {
        string manual = Config.Current.City.Trim();

        if (manual.Length > 0)
        {
            // 手动城市优先重新地理编码,避免市级/失败的陈旧缓存被长期沿用(缓存仅作编码失败回退)
            var g = await AmapGeocodeAsync(NormalizeManualAddress(manual));
            if (g != null) return (g.Lat, g.Lon, g.City, g.Adcode ?? "");
        }

        if (TryReadCache(out var cLat, out var cLon, out var cCity, out var cAdcode) &&
            !string.IsNullOrEmpty(cAdcode))
        {
            string label = manual.Length > 0 ? manual : cCity;
            return (cLat, cLon, label, cAdcode);
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

    /// <summary>手动地址纠形:「朝阳区·北京市」的 · 是展示分隔不是地址,
    /// 还原为「北京市朝阳区」(行政级别大的在前),否则高德会解析失败或落到市级。</summary>
    public static string NormalizeManualAddress(string name)
    {
        string trimmed = name.Trim();
        var parts = trimmed.Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length <= 1) return trimmed;
        // 省/市这类高级单位排前(Amap 上级在前解析最准)
        var ordered = parts.OrderByDescending(Rank).ToArray();
        static int Rank(string s) =>
            s.Contains("自治区") ? 3 : (s.Contains("省") || s.Contains("市") || s.Contains("州")) ? 2 :
            (s.Contains("区") || s.Contains("县") || s.Contains("旗")) ? 1 : 0;
        return string.Concat(ordered);
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
                  "&current=temperature_2m,weather_code,wind_speed_10m,wind_direction_10m" +
                  "&hourly=uv_index&forecast_hours=1" +
                  "&daily=temperature_2m_max,temperature_2m_min" +
                  "&timezone=auto&forecast_days=1";
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
        var root = doc.RootElement;
        var cur = root.GetProperty("current");
        double temp = cur.GetProperty("temperature_2m").GetDouble();
        int code = cur.GetProperty("weather_code").GetInt32();
        double windKmh = cur.GetProperty("wind_speed_10m").GetDouble();
        string windDir = cur.TryGetProperty("wind_direction_10m", out var wdir)
            ? DegToCnDir(wdir.GetDouble()) : "";
        double uv = root.GetProperty("hourly").GetProperty("uv_index")[0].GetDouble();
        double tmax = root.GetProperty("daily").GetProperty("temperature_2m_max")[0].GetDouble();
        double tmin = root.GetProperty("daily").GetProperty("temperature_2m_min")[0].GetDouble();

        var (aqi, aqiLevel) = await FetchAirQualityAsync(lat, lon);
        return new WeatherInfo(temp, WmoDescription(code), tmax, tmin, city,
            Beaufort(windKmh).ToString(), windDir, windKmh, uv, UvLevel(uv), aqi, aqiLevel);
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

    private async Task<(double uv, string uvLevel, int? aqi, string aqiLevel, double? windKmh)> FetchEnhancementsAsync(double lat, double lon)
    {
        double? uv = null;
        string uvLevel = "—";
        int? aqi = null;
        string aqiLevel = "—";
        double? wind = null;

        // 和风优先(国内口径:站实况污染/今日最大 UV/实测风),任一项缺失再回落 Open-Meteo
        if (HasQWeather && await FetchQWeatherEnhancementsAsync(lat, lon) is { } q)
        {
            uv = q.Uv;
            aqi = q.Aqi;
            wind = q.WindKmh;
        }

        if (uv == null)
        {
            try
            {
                var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat:F4}&longitude={lon:F4}" +
                          "&hourly=uv_index&forecast_hours=1&timezone=auto";
                using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
                uv = doc.RootElement.GetProperty("hourly").GetProperty("uv_index")[0].GetDouble();
            }
            catch { }
        }
        if (uv is { } uvv && uvv >= 0)
            uvLevel = UvLevel(uvv);

        // 真实 10m 风速,单独特容(高德源只有风级,不编 km/h)
        if (wind == null)
        {
            try
            {
                var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat:F4}&longitude={lon:F4}" +
                          "&current=wind_speed_10m&timezone=auto";
                using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
                double v = doc.RootElement.GetProperty("current").GetProperty("wind_speed_10m").GetDouble();
                if (v >= 0) wind = v;
            }
            catch { }
        }

        if (aqi == null)
            (aqi, aqiLevel) = await FetchAirQualityAsync(lat, lon);

        return (uv ?? 0, uvLevel, aqi, aqiLevel, wind);
    }

    private async Task<(int? aqi, string level)> FetchAirQualityAsync(double lat, double lon)
    {
        try
        {
            var url = $"https://air-quality-api.open-meteo.com/v1/air-quality?latitude={lat:F4}&longitude={lon:F4}" +
                      "&current=pm2_5,pm10,ozone,nitrogen_dioxide,carbon_monoxide,sulphur_dioxide&timezone=auto";
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(url));
            var cur = doc.RootElement.GetProperty("current");
            int aqi = CnAqi(
                Opt(cur, "pm2_5"), Opt(cur, "pm10"), Opt(cur, "ozone"),
                Opt(cur, "nitrogen_dioxide"), Opt(cur, "carbon_monoxide"), Opt(cur, "sulphur_dioxide"));
            return (aqi, AqiLevel(aqi));
        }
        catch
        {
            return (null, "");
        }
    }

    private static double? Opt(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;

    // ---------- 中国标准 AQI(HJ 633-2012) ----------
    // Open-Meteo 无现成 cn_aqi,按污染物浓度用 HJ633 断点表自算 IAQI 取最大值。
    // 实时发布近似:小时代入 PM 的 24h 断点(监测网络通用做法),NO2/SO2/CO/O3 用 1h 断点。

    private static readonly (double Cut, int Ia)[] Pm25Bps =
        { (35,50),(75,100),(115,150),(150,200),(250,300),(350,400),(500,500) };
    private static readonly (double Cut, int Ia)[] Pm10Bps =
        { (50,50),(150,100),(250,150),(350,200),(420,300),(500,400),(600,500) };
    private static readonly (double Cut, int Ia)[] O3Bps =
        { (160,50),(200,100),(300,150),(400,200),(800,300),(1000,400),(1200,500) };      // 1h
    private static readonly (double Cut, int Ia)[] No2Bps =
        { (100,50),(200,100),(700,150),(1200,200),(2340,300),(3090,400),(3840,500) };    // 1h
    private static readonly (double Cut, int Ia)[] CoBps =
        { (10,50),(35,100),(60,150),(90,200),(120,300),(150,400),(180,500) };            // 1h, mg/m³
    private static readonly (double Cut, int Ia)[] So2Bps =
        { (150,50),(500,100),(650,150),(800,200),(1600,300),(2650,400),(3850,500) };     // 1h

    private static int Iaqi((double Cut, int Ia)[] bps, double c)
    {
        double cLow = 0, iLow = 0;
        foreach (var (cut, ia) in bps)
        {
            if (c <= cut)
                return (int)Math.Round((c - cLow) / (cut - cLow) * (ia - iLow) + iLow);
            cLow = cut; iLow = ia;
        }
        return bps[^1].Ia; // 超表封顶(500,仍属严重污染)
    }

    private static int CnAqi(double? pm25, double? pm10, double? o3, double? no2, double? coUgM3, double? so2)
    {
        int best = 0;
        void Take(double? v, (double Cut, int Ia)[] bps, double div = 1)
        {
            if (v is { } x && x >= 0 && double.IsFinite(x)) best = Math.Max(best, Iaqi(bps, x / div));
        }
        Take(pm25, Pm25Bps); Take(pm10, Pm10Bps); Take(o3, O3Bps);
        Take(no2, No2Bps); Take(coUgM3, CoBps, 1000); Take(so2, So2Bps);
        return best; // 浓度全可用时为 0 的可能性仅限全部接近 0,正常不会触发
    }

    // ==================== 和风天气(QWeather 新 API,国内口径) ====================
    // 开发者专属 API Host(控制台首页,形如 abcxyz.qweatherapi.com)+ API KEY 凭据(X-QW-Api-Key 头)。
    // 主源链 FetchQWeatherMainAsync:实时 /weather/v1/current + 逐日 /weather/v1/daily?days=1 +
    // 空气 /airquality/v1/current,并行 3 请求 —— 温度/天气现象(condition.text)/风级风向/湿度(0-1→%)、
    // 今日 UV(uvIndexMax,0-15)/AQI 全国内口径;实时失败整链回落高德/Open-Meteo,
    // 逐日/空气失败仅丢对应项(高低温沿用当前温度、UV 用实时的 uvIndex、AQI 隐藏)。
    // 增强链 FetchQWeatherEnhancementsAsync:高德/Open-Meteo 主源时只取 UV/AQI/风速三项,逐项回落。
    // 空气质量 indexes[] 中中国标准项 code 含 "cn"(如 cn-mep;没有时按 pollutants[] 浓度用 HJ 633-2012 自算)。

    private sealed record QwEnhancements(double? Uv, int? Aqi, double? WindKmh);

    private async Task<WeatherInfo?> FetchQWeatherMainAsync()
    {
        try
        {
            var (lat, lon, city) = await ResolveLocationAsync();
            string host = Config.Current.QWeatherHost.Trim().TrimEnd('/');
            string key = Config.Current.QWeatherKey.Trim();
            string la = lat.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            string lo = lon.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

            var nowTask = QwGetAsync(key, host, $"/weather/v1/current/{la}/{lo}");
            var dailyTask = QwGetAsync(key, host, $"/weather/v1/daily/{la}/{lo}?days=1");
            var aqTask = QwGetAsync(key, host, $"/airquality/v1/current/{la}/{lo}");
            await Task.WhenAll(nowTask, dailyTask, aqTask);
            if (nowTask.Result is not { } cur) return null;

            string desc = cur.TryGetProperty("condition", out var cond) ? Str(cond, "text") : "";
            double? temp = QwNestedNum(cur, "temperature");
            if (desc.Length == 0 || temp == null) return null;   // 核心字段缺失 → 视为不可用,整链回落

            int? humidity = cur.TryGetProperty("humidity", out var h) && h.ValueKind == JsonValueKind.Number
                ? (int)Math.Round(h.GetDouble() * 100) : null;

            string windLvlTxt = "", windDir = "";
            double? windKmh = null;
            if (cur.TryGetProperty("wind", out var w))
            {
                if (w.TryGetProperty("scale", out var sc) && sc.ValueKind == JsonValueKind.Number)
                    windLvlTxt = ((int)Math.Round(sc.GetDouble())).ToString();   // 和风是确定风级,无「≤」形态
                if (w.TryGetProperty("direction", out var dir) &&
                    dir.TryGetProperty("degree", out var dg) && dg.ValueKind == JsonValueKind.Number)
                    windDir = DegToCnDir(dg.GetDouble());
                if (w.TryGetProperty("speed", out var sp) && sp.TryGetProperty("value", out var sv) &&
                    sv.ValueKind == JsonValueKind.Number && sv.GetDouble() >= 0)
                    windKmh = sv.GetDouble() * 3.6;              // 和风风速单位 m/s → 面板 km/h
            }

            double? uv = null;
            string uvLevel = "—";
            double tmax = temp.Value, tmin = temp.Value;
            if (dailyTask.Result is { } d &&
                d.TryGetProperty("daily", out var arr) && arr.ValueKind == JsonValueKind.Array &&
                arr.GetArrayLength() > 0)
            {
                var day = arr[0];
                tmax = QwNestedNum(day, "temperatureMax") ?? tmax;
                tmin = QwNestedNum(day, "temperatureMin") ?? tmin;
                if (day.TryGetProperty("uvIndexMax", out var uvm) && uvm.ValueKind == JsonValueKind.Number &&
                    uvm.GetDouble() >= 0)
                {
                    uv = uvm.GetDouble();
                    uvLevel = UvLevel(uv.Value);
                }
            }
            if (uv == null && cur.TryGetProperty("uvIndex", out var cu) &&
                cu.ValueKind == JsonValueKind.Number && cu.GetDouble() >= 0)
            {
                uv = cu.GetDouble();                             // 逐日缺 uvIndexMax 时用实时的当前值兜底
                uvLevel = UvLevel(uv.Value);
            }

            int? aqi = null;
            string aqiLevel = "—";
            if (aqTask.Result is { } a)
            {
                aqi = QwCnAqi(a);
                aqiLevel = aqi is { } v ? AqiLevel(v) : "—";
            }

            return new WeatherInfo(temp.Value, desc, tmax, tmin, city,
                windLvlTxt, windDir, windKmh, uv ?? 0, uvLevel, aqi, aqiLevel, humidity);
        }
        catch
        {
            return null;
        }
    }

    private async Task<QwEnhancements?> FetchQWeatherEnhancementsAsync(double lat, double lon)
    {
        try
        {
            string host = Config.Current.QWeatherHost.Trim().TrimEnd('/');
            string key = Config.Current.QWeatherKey.Trim();
            string la = lat.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            string lo = lon.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

            var aqTask = QwGetAsync(key, host, $"/airquality/v1/current/{la}/{lo}");
            var uvTask = QwGetAsync(key, host, $"/weather/v1/daily/{la}/{lo}?days=1");
            var nowTask = QwGetAsync(key, host, $"/weather/v1/current/{la}/{lo}");
            await Task.WhenAll(aqTask, uvTask, nowTask);

            double? uv = uvTask.Result is { } d ? QwUvIndex(d) : null;
            int? aqi = aqTask.Result is { } a ? QwCnAqi(a) : null;
            double? wind = null;
            if (nowTask.Result is { } cur &&
                cur.TryGetProperty("wind", out var w) && w.TryGetProperty("speed", out var sp) &&
                sp.TryGetProperty("value", out var sv) && sv.ValueKind == JsonValueKind.Number &&
                sv.GetDouble() >= 0)
                wind = sv.GetDouble() * 3.6;             // 和风新 API 风速单位 m/s → 面板 km/h

            if (uv == null && aqi == null && wind == null) return null;
            return new QwEnhancements(uv, aqi, wind);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>GET 和风新 API:X-QW-Api-Key 头;非 2xx、信封 code≠"200" 或解析失败一律返回 null
    /// (增强项失败静默,与 Open-Meteo 增强同一容错风格)。</summary>
    private async Task<JsonElement?> QwGetAsync(string key, string host, string pathAndQuery)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{host}{pathAndQuery}");
            req.Headers.TryAddWithoutValidation("X-QW-Api-Key", key);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement.Clone();
            string code = Str(root, "code");
            if (code.Length > 0 && code != "200") return null;
            return root;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>daily[0].uvIndexMax(今日最大紫外线,0-15);缺失/非数字返回 null。</summary>
    private static double? QwUvIndex(JsonElement root)
    {
        if (root.TryGetProperty("daily", out var d) && d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0 &&
            d[0].TryGetProperty("uvIndexMax", out var u) && u.ValueKind == JsonValueKind.Number)
        {
            double v = u.GetDouble();
            if (v >= 0) return v;
        }
        return null;
    }

    /// <summary>indexes[] 里取中国标准 AQI(code 含 "cn" 的项);没有则按 pollutants[] 浓度
    /// 用 HJ 633-2012 自算(CO 单位 μg/m³,CnAqi 内部换算 mg/m³)。</summary>
    private static int? QwCnAqi(JsonElement root)
    {
        if (root.TryGetProperty("indexes", out var idx) && idx.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in idx.EnumerateArray())
            {
                string code = Str(it, "code");
                if (!code.ToLowerInvariant().Contains("cn")) continue;
                if (it.TryGetProperty("aqi", out var v) && v.ValueKind == JsonValueKind.Number)
                    return (int)Math.Round(v.GetDouble());
            }
        }
        if (root.TryGetProperty("pollutants", out var pol) && pol.ValueKind == JsonValueKind.Array)
        {
            double? pm25 = null, pm10 = null, o3 = null, no2 = null, co = null, so2 = null;
            foreach (var it in pol.EnumerateArray())
            {
                double? val = it.TryGetProperty("concentration", out var c) &&
                              c.TryGetProperty("value", out var cv) && cv.ValueKind == JsonValueKind.Number
                    ? cv.GetDouble() : null;
                if (val is not { } x) continue;
                switch (Str(it, "code").ToLowerInvariant())
                {
                    case "pm2p5" or "pm25": pm25 = x; break;
                    case "pm10": pm10 = x; break;
                    case "o3": o3 = x; break;
                    case "no2": no2 = x; break;
                    case "co": co = x; break;
                    case "so2": so2 = x; break;
                }
            }
            if (pm25 != null || pm10 != null || o3 != null || no2 != null || co != null || so2 != null)
                return CnAqi(pm25, pm10, o3, no2, co, so2);
        }
        return null;
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

    /// <summary>obj.{container}.value 的数值(和风新 API 度量字段统一为 {value,unit} 形态)。</summary>
    private static double? QwNestedNum(JsonElement obj, string container) =>
        obj.TryGetProperty(container, out var c) && c.TryGetProperty("value", out var v) &&
        v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

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

    /// <summary>风向角(风来的方向,0=北)→ 中文八方;非法角度返回空(不显示)。</summary>
    private static string DegToCnDir(double deg)
    {
        if (double.IsNaN(deg) || deg < -360 || deg > 360) return "";
        string[] dirs = { "北", "东北", "东", "东南", "南", "西南", "西", "西北" };
        return dirs[(((int)Math.Round(deg / 45.0) % 8) + 8) % 8];
    }

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
