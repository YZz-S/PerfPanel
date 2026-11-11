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
    public double? LastPrefill { get; init; }      // /metrics.last_timings.prompt_per_second(上次请求输入速度)
    public double? LastDecode { get; init; }       // /metrics.last_timings.predicted_per_second(上次请求输出速度)
    public double? HitRate { get; init; }          // 投机解码命中率 drafts_accepted/offered(服务端有统计时)
}

/// <summary>
/// Strata 本地推理监控。数据全部来自引擎本机 HTTP API(默认 127.0.0.1:8080;桌面端 Web UI 的端口是
/// 另一套 SPA 代理,并不暴露这些端点。引擎只在加载模型后监听端口,空闲/未加载时探测失败即「未运行」):
/// /health 免鉴权做加载判定并返回 model;/status 给 busy/queued/tokens_per_s_mean,/metrics 的
/// last_timings 给上次请求的 prefill·decode 速度。设置了 API Key 的服务(设置里填,面板带 Bearer 头)
/// 除 /health 外一律要求密钥,401/403 即提示密钥无效。速度口径:Busy 时显示 tokens_per_s_mean 的
/// 实时滚动平均;Last% 是服务端显式给出的「上次请求」值,不过期。
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
        int port = c.StrataPort;
        var snap = new StrataSnapshot { AuthOk = true };

        snap = snap with { Online = await ProbeAsync(port) };
        if (!snap.Online)
        {
            Current = snap;   // 引擎没在跑:直接换空快照
            return;
        }

        string? key = c.StrataApiKey.Trim();
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
            double? live =
                status.El.TryGetProperty("tokens_per_s_mean", out var lpsEl) &&
                lpsEl.ValueKind == JsonValueKind.Number &&
                lpsEl.GetDouble() is > 0 and < 5000 ? lpsEl.GetDouble() : null;
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
        else if (metrics.El.ValueKind == JsonValueKind.Object &&
                 metrics.El.TryGetProperty("last_timings", out var lt) && lt.ValueKind == JsonValueKind.Object)
        {
            double? prefill =
                lt.TryGetProperty("prompt_per_second", out var ppEl) && ppEl.ValueKind == JsonValueKind.Number &&
                ppEl.GetDouble() is > 0 and < 20000 ? ppEl.GetDouble() : null;
            double? decode =
                lt.TryGetProperty("predicted_per_second", out var pdEl) && pdEl.ValueKind == JsonValueKind.Number &&
                pdEl.GetDouble() is > 0 and < 5000 ? pdEl.GetDouble() : null;
            double? hit = null;
            if (metrics.El.TryGetProperty("drafts_offered", out var dfEl) &&
                metrics.El.TryGetProperty("drafts_accepted", out var daEl) &&
                dfEl.ValueKind == JsonValueKind.Number && daEl.ValueKind == JsonValueKind.Number &&
                dfEl.GetInt32() > 0)
            {
                hit = (double)daEl.GetInt32() / dfEl.GetInt32();   // 投机解码接受率
                if (hit is not (> 0 and <= 1)) hit = null;
            }
            snap = snap with { LastPrefill = prefill, LastDecode = decode, HitRate = hit };
        }

        Current = snap;
    }

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
            if (key is { Length: > 0 })
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
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
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            return client.Connected;
        }
        catch { return false; }
    }
}
