using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace PerfPanel.Services;

/// <summary>Strata 快照:不可变,后台轮询整体换新,UI 线程直接读。</summary>
public sealed record StrataSnapshot
{
    public bool Online { get; init; }              // 端口可连(引擎在跑;桌面端装配的模型服务)
    public bool AuthOk { get; init; }              // 配置了密钥且被服务端接受;401/403 = false
    public bool Loaded { get; init; }              // 模型已驻留(/health.loaded)
    public string? ModelId { get; init; }          // /health.model
    public bool Busy { get; init; }                // /status.busy(正在处理请求)
    public int Queued { get; init; }               // /status.queued(排队请求数)
    public string? Phase { get; init; }            // /status.phase(busy 时的处理阶段,如 generating)
    public double? LiveDecode { get; init; }       // /status.tokens_per_s_mean(busy 时的滚动平均输出速度)
    public double? LastPrefill { get; init; }      // 上次请求输入速度(prompt_tokens÷prompt_ms)
    public double? LastDecode { get; init; }       // 上次请求输出速度(decode_tok_s)
    public double? HitRate { get; init; }          // 投机解码命中率 drafts_accepted/offered
    public int Port { get; init; }                 // 本次实际探测的端口
    public bool AutoPort { get; init; }            // true = 端口/密钥自动发现(配置项已失效)
}

/// <summary>
/// Strata 本地推理监控。数据全部来自引擎本机 HTTP API:
/// /health 免鉴权做加载判定并返回 model;/status 给 busy/queued/tokens_per_s_mean;/metrics 给上次请求速度。
/// 设置了 API Key 的服务除 /health 外一律要求密钥,401/403 即提示密钥无效。
///
/// 端口与密钥的坑:Strata 桌面端拉起的引擎走<b>动态端口</b>(18500 / 18501)且<b>每次启动重新生成 API Key</b>
/// (写进 ~/.strata/data/native-instances/*.json 的 api_key),所以「设置里写死端口 + 手填 Key」必然过期,
/// 表现为面板永远「服务未运行」。故优先用配置端口探测,不通(或 401)则自动发现:
/// %LOCALAPPDATA%\Strata\runtime\*.ready.json 里取最新且进程仍存活的那份拿 bound_port/pid,
/// 再按端口从 native-instances 里匹配最近写入的 api_key。
///
/// 速度口径:Busy 时显示 /status.tokens_per_s_mean 的实时滚动平均;Last% 是「上次完成的请求」——
/// 0.1.5x 起 /metrics 不再有顶层 last_timings,改从 requests[] 里取 time 最大的一条
/// (prefill = prompt_tokens ÷ prompt_ms/1000,decode = decode_tok_s),旧口径仍作回落。
/// </summary>
public sealed class StrataService
{
    public static TimeSpan PollInterval => TimeSpan.FromSeconds(2);

    public StrataSnapshot Current { get; private set; } = new();

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2.5) };

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task PollAsync()
    {
        var c = Config.Current;
        var ep = await PickEndpointAsync(c);
        if (ep is not { } e)
        {
            Current = new StrataSnapshot { Port = Math.Max(0, c.StrataPort) };  // 引擎确实没跑
            return;
        }

        var snap = await FetchAsync(e.Port, e.Key);
        snap = snap with { Port = e.Port, AutoPort = e.Auto };

        // 配置里的 Key 过期(引擎换 Key)→ /status·/metrics 401:换自动发现的实例 Key 再拉一次
        if (!snap.AuthOk)
        {
            var disc = await DiscoverAsync();
            if (disc is { Port: var dp, Key: { Length: > 0 } ak } && dp == e.Port && ak != e.Key)
                snap = (await FetchAsync(e.Port, ak)) with { Port = e.Port, AutoPort = true };
        }

        Current = snap;
    }

    // ---------- 端点选择 ----------

    private readonly record struct Endpoint(int Port, string? Key, bool Auto);

    /// <summary>先按配置(尊重手填端口/Key),不通再自动发现引擎真实端点。</summary>
    private async Task<Endpoint?> PickEndpointAsync(AppConfig c)
    {
        string cfgKey = c.StrataApiKey.Trim();
        if (c.StrataPort > 0 && await ProbeAsync(c.StrataPort))
            return new Endpoint(c.StrataPort, cfgKey, false);

        var disc = await DiscoverAsync();
        if (disc is { } d && await ProbeAsync(d.Port))
            return new Endpoint(d.Port, d.Key is { Length: > 0 } k ? k : cfgKey, true);

        return null;
    }

    // ---------- 单次抓取 ----------

    private async Task<StrataSnapshot> FetchAsync(int port, string? key)
    {
        var snap = new StrataSnapshot { AuthOk = true, Online = true };
        string baseUrl = $"http://127.0.0.1:{port}";

        // /health 免鉴权(源码无鉴权检查):loaded / model
        var health = await GetJsonAsync(baseUrl + "/health", key);
        if (health.IsAuthError) snap = snap with { AuthOk = false };
        else if (health.El.ValueKind == JsonValueKind.Object)
        {
            bool loaded =
                health.El.TryGetProperty("loaded", out var loadEl) &&
                (loadEl.ValueKind == JsonValueKind.True ||
                 (loadEl.ValueKind == JsonValueKind.Number && loadEl.GetDouble() != 0));
            string? model = null;
            if (health.El.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String &&
                mEl.GetString() is { Length: > 0 } mid)
                model = mid;
            snap = snap with { Loaded = loaded, ModelId = model };
        }

        // 以下两个端点在设置了密钥的服务上必须带 Bearer,否则 401/403
        var status = await GetJsonAsync(baseUrl + "/status", key);
        if (status.IsAuthError) snap = snap with { AuthOk = false };
        else if (status.El.ValueKind == JsonValueKind.Object)
        {
            bool busy =
                status.El.TryGetProperty("busy", out var busyEl) &&
                busyEl.ValueKind is JsonValueKind.True or JsonValueKind.False && busyEl.GetBoolean();
            int queued =
                status.El.TryGetProperty("queued", out var qEl) && qEl.ValueKind == JsonValueKind.Number
                    ? qEl.GetInt32() : 0;
            double? live = Num(status.El, "tokens_per_s_mean");
            if (live is not (> 0 and < 5000)) live = null;
            string? phase = null;
            if (status.El.TryGetProperty("phase", out var phEl) && phEl.ValueKind == JsonValueKind.String &&
                phEl.GetString() is { Length: > 0 } ph)
                phase = ph;
            snap = snap with
            {
                Busy = busy, Queued = Math.Max(0, queued), Phase = phase,
                LiveDecode = busy ? live : null,
            };
        }

        var metrics = await GetJsonAsync(baseUrl + "/metrics", key);
        if (metrics.IsAuthError) snap = snap with { AuthOk = false };
        else if (metrics.El.ValueKind == JsonValueKind.Object)
            snap = WithLastTimings(snap, metrics.El);

        return snap;
    }

    // ---------- 自动发现(动态端口 + 每次启动换 Key) ----------

    private sealed record Discovered(int Port, string? Key);

    private Discovered? _discovered;
    private DateTime _discoveredUtc;
    private static readonly TimeSpan DiscoverTtl = TimeSpan.FromSeconds(20);

    private async Task<Discovered?> DiscoverAsync()
    {
        if (_discovered is not null && DateTime.UtcNow - _discoveredUtc < DiscoverTtl)
            return _discovered;
        _discovered = await DiscoverCoreAsync();
        _discoveredUtc = DateTime.UtcNow;
        return _discovered;
    }

    private static async Task<Discovered?> DiscoverCoreAsync()
    {
        try
        {
            int port = await FindLivePortAsync();
            if (port <= 0) return null;
            return new Discovered(port, await FindKeyAsync(port));
        }
        catch { return null; }
    }

    /// <summary>最新的 ready.json 里 bound_port 对应的进程仍存活 → 就是当前引擎端口。</summary>
    private static async Task<int> FindLivePortAsync()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Strata", "runtime");
        if (!Directory.Exists(dir)) return 0;

        int port = 0, pid = 0;
        DateTime newest = DateTime.MinValue;
        foreach (string f in Directory.EnumerateFiles(dir, "*.ready.json"))
        {
            DateTime at = File.GetLastWriteTimeUtc(f);
            if (at <= newest) continue;
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(f));
                var r = doc.RootElement;
                if (Num(r, "bound_port") is not { } p || p <= 0) continue;
                if (Num(r, "pid") is not { } d || d <= 0) continue;
                port = (int)p;
                pid = (int)d;
                newest = at;
            }
            catch { /* 单个文件坏了跳过 */ }
        }
        if (port <= 0 || !IsAlive(pid)) return 0;   // 文件在但进程已退 → 陈旧
        return port;
    }

    /// <summary>native-instances 里端口相同的、最近写入的那份带本次启动的 api_key。</summary>
    private static async Task<string?> FindKeyAsync(int port)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".strata", "data", "native-instances");
        if (!Directory.Exists(dir)) return null;

        string? key = null;
        DateTime newest = DateTime.MinValue;
        foreach (string f in Directory.EnumerateFiles(dir, "*.json"))
        {
            DateTime at = File.GetLastWriteTimeUtc(f);
            if (at <= newest) continue;
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(f));
                var r = doc.RootElement;
                if (Num(r, "port") is not { } p || (int)p != port) continue;
                if (!r.TryGetProperty("api_key", out var ke) || ke.ValueKind != JsonValueKind.String) continue;
                if (ke.GetString() is not { Length: > 0 } k) continue;
                key = k;
                newest = at;
            }
            catch { /* 单个文件坏了跳过 */ }
        }
        return key;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    // ---------- HTTP ----------

    private readonly record struct HttpResult(JsonElement El, int Code)
    {
        public bool IsAuthError => Code is 401 or 403;
    }

    /// <summary>HTTP GET;Code = 200(拿到 JSON 对象)/ 401·403(鉴权拒绝)/ -1 其他状态码 / -2 非 JSON / -3 网络·异常。</summary>
    private async Task<HttpResult> GetJsonAsync(string url, string? key)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (key is { Length: > 0 } k)
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", k);
            using var resp = await _http.SendAsync(req);
            int code = (int)resp.StatusCode;
            if (code is 401 or 403) return new HttpResult(default, code);
            if (!resp.IsSuccessStatusCode) return new HttpResult(default, -1);
            string text = await resp.Content.ReadAsStringAsync();
            try
            {
                var el = JsonSerializer.Deserialize<JsonElement>(text, JsonOpts);
                return el.ValueKind == JsonValueKind.Object
                    ? new HttpResult(el, 200)
                    : new HttpResult(default, -2);
            }
            catch { return new HttpResult(default, -2); }
        }
        catch
        {
            return new HttpResult(default, -3);
        }
    }

    /// <summary>端口可连 = 引擎在跑(纯 TCP,与 LM Studio 同款探测)。</summary>
    private static async Task<bool> ProbeAsync(int port)
    {
        if (port <= 0) return false;
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            return client.Connected;
        }
        catch { return false; }
    }

    internal static double? Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetDouble() : null;

    /// <summary>/metrics 口径迁移:0.1.5x 起无顶层 last_timings,改从 requests[] 取最近完成的一条。</summary>
    private static StrataSnapshot WithLastTimings(StrataSnapshot snap, JsonElement metrics)
    {
        double? prefill = null, decode = null, hit = null;

        // 旧口径(≤0.1.4x):last_timings.prompt_per_second / predicted_per_second
        if (metrics.TryGetProperty("last_timings", out var lt) && lt.ValueKind == JsonValueKind.Object)
        {
            if (Num(lt, "prompt_per_second") is { } pp && pp is > 0 and < 20000) prefill = pp;
            if (Num(lt, "predicted_per_second") is { } pd && pd is > 0 and < 5000) decode = pd;
        }

        // 新口径:requests[] 取 time 最大(最近完成)的一条
        if (metrics.TryGetProperty("requests", out var reqs) && reqs.ValueKind == JsonValueKind.Array)
        {
            JsonElement? last = null;
            double lastT = double.MinValue;
            foreach (var r in reqs.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                double t = Num(r, "time") ?? 0;
                if (last is null || t > lastT) { last = r; lastT = t; }
            }
            if (last is { } lr)
            {
                // prefill:服务端只给 prompt_tokens + prompt_ms,换回 tok/s
                if (Num(lr, "prompt_tokens") is { } pt && Num(lr, "prompt_ms") is { } pms && pms > 0)
                {
                    double v = pt / (pms / 1000.0);
                    if (v is > 0 and < 20000) prefill = v;
                }
                if (Num(lr, "decode_tok_s") is { } dt && dt is > 0 and < 5000) decode = dt;

                double? off = Num(lr, "drafts_offered"), acc = Num(lr, "drafts_accepted");
                if (off is > 0 && acc is not null)
                {
                    double h = acc.Value / off.Value;
                    if (h is > 0 and <= 1) hit = h;
                }
            }
        }

        return snap with { LastPrefill = prefill, LastDecode = decode, HitRate = hit };
    }
}
