using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PerfPanel.Services;

/// <summary>单个限额窗口(5h/周/月)或余额条目。</summary>
/// <param name="UsedPercent">已用百分比 0-100;余额类为 null。</param>
/// <param name="RemainingValue">剩余绝对值(金额/点数);百分比类为 null。</param>
public sealed record QuotaWindow(
    string Label, double? UsedPercent, double? RemainingValue, double? TotalValue, DateTime? ResetUtc);

/// <summary>一个供应商的额度状态。Ok=false 且 Error 非空 = 当前不可用;
/// Ok=true 且 StaleError 非空 = 显示的是上次成功快照(本次刷新失败)。</summary>
public sealed record PlanStatus
{
    public required string Name { get; init; }
    public bool Ok { get; init; }
    public string? PlanName { get; init; }
    public string? Unit { get; init; }                  // CNY / 点;null = 百分比
    public IReadOnlyList<QuotaWindow> Windows { get; init; } = [];
    public string? Error { get; init; }
    public string? StaleError { get; init; }
}

/// <summary>Coding Plan 额度服务:DeepSeek 余额(官方)、智谱 GLM(非官方)、火山方舟(非官方)、
/// Xiaomi MiMo(控制台 Cookie:按量付费余额 + Token Plan 用量)、OpenCode Go(sk- API Key)。
/// 刷新失败保留上次成功值;各供应商相互独立容错。</summary>
public sealed class CodingPlanService
{
    public static TimeSpan RefreshInterval => TimeSpan.FromMinutes(15);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Dictionary<string, PlanStatus> _last = [];

    public IReadOnlyList<PlanStatus> Current { get; private set; } = [];
    public DateTime LastRefreshUtc { get; private set; }
    public bool AnyKeyConfigured
    {
        get
        {
            var c = Config.Current;
            return !string.IsNullOrWhiteSpace(c.DeepSeekKey)
                || !string.IsNullOrWhiteSpace(c.ZhipuKey)
                || !string.IsNullOrWhiteSpace(c.MimoCookie)
                || !string.IsNullOrWhiteSpace(c.OpenCodeKey)
                || (!string.IsNullOrWhiteSpace(c.VolcAk) && !string.IsNullOrWhiteSpace(c.VolcSk));
        }
    }

    /// <summary>并行刷新全部已配置的供应商,结果写入 Current。</summary>
    public async Task<IReadOnlyList<PlanStatus>> FetchAllAsync()
    {
        var c = Config.Current;
        var queries = new List<Task<PlanStatus>>();
        if (!string.IsNullOrWhiteSpace(c.DeepSeekKey)) queries.Add(QueryDeepSeekAsync(c.DeepSeekKey.Trim()));
        if (!string.IsNullOrWhiteSpace(c.ZhipuKey)) queries.Add(QueryZhipuAsync(c.ZhipuKey.Trim()));
        if (!string.IsNullOrWhiteSpace(c.MimoCookie)) queries.Add(QueryMimoAsync(c.MimoCookie.Trim()));
        if (!string.IsNullOrWhiteSpace(c.VolcAk) && !string.IsNullOrWhiteSpace(c.VolcSk))
            queries.Add(QueryVolcAsync(c.VolcAk.Trim(), c.VolcSk.Trim()));
        if (!string.IsNullOrWhiteSpace(c.OpenCodeKey)) queries.Add(QueryOpenCodeAsync(c.OpenCodeKey.Trim()));

        if (queries.Count > 0)
        {
            var results = await Task.WhenAll(queries);
            Current = results.Select(Merge).ToList();
            LastRefreshUtc = DateTime.UtcNow;
        }
        else
        {
            Current = [];
        }
        return Current;
    }

    /// <summary>成功覆盖缓存;失败保留旧快照并附 StaleError,无旧快照才透出错误。</summary>
    private PlanStatus Merge(PlanStatus fresh)
    {
        if (fresh.Ok)
        {
            _last[fresh.Name] = fresh;
            return fresh;
        }
        if (_last.TryGetValue(fresh.Name, out var old) && old.Ok)
            return old with { StaleError = fresh.Error };
        _last[fresh.Name] = fresh;
        return fresh;
    }

    private static PlanStatus Ok(string name, IReadOnlyList<QuotaWindow> windows,
        string? plan = null, string? unit = null) =>
        new() { Name = name, Ok = true, Windows = windows, PlanName = plan, Unit = unit };

    private static PlanStatus Err(string name, string error) =>
        new() { Name = name, Ok = false, Error = error };

    // ==================== DeepSeek(官方接口) ====================
    // GET https://api.deepseek.com/user/balance
    // { is_available, balance_infos: [{ currency, total_balance, ... }] }

    public async Task<PlanStatus> QueryDeepSeekAsync(string key)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var resp = await _http.SendAsync(req);
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return Err("DeepSeek", $"鉴权失败(HTTP {(int)resp.StatusCode}),请检查 API Key");
            if (!resp.IsSuccessStatusCode)
                return Err("DeepSeek", $"API 错误(HTTP {(int)resp.StatusCode})");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            bool available = root.TryGetProperty("is_available", out var av) && av.GetBoolean();
            if (root.TryGetProperty("balance_infos", out var infos) && infos.GetArrayLength() > 0)
            {
                var info = infos[0];
                string currency = Str(info, "currency");
                if (currency.Length == 0) currency = "CNY";
                double total = ParseD(info, "total_balance");
                var w = new List<QuotaWindow>
                {
                    new("余额", null, total, ParseD(info, "topped_up_balance") + ParseD(info, "granted_balance"), null),
                };
                return Ok("DeepSeek", w, null, currency);
            }
            return Err("DeepSeek", available ? "响应中没有余额信息" : "账户不可用(余额不足或被禁用)");
        }
        catch (Exception ex)
        {
            return Err("DeepSeek", $"网络错误:{ex.Message}");
        }
    }

    // ==================== 智谱 GLM Coding Plan(非官方) ====================
    // GET https://open.bigmodel.cn/api/monitor/usage/quota/limit
    // Authorization 头直接放 API Key(不加 Bearer)。
    // data.limits[]: TOKENS_LIMIT 条目,unit=3 是 5h 窗、unit=6 是周窗;
    // percentage 为已用百分比,nextResetTime 为毫秒时间戳。

    public async Task<PlanStatus> QueryZhipuAsync(string key)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://open.bigmodel.cn/api/monitor/usage/quota/limit");
            req.Headers.TryAddWithoutValidation("Authorization", key); // 智谱不加 Bearer 前缀
            req.Headers.AcceptLanguage.ParseAdd("en-US,en");
            using var resp = await _http.SendAsync(req);
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return Err("GLM", $"鉴权失败(HTTP {(int)resp.StatusCode}),请检查 API Key");
            if (!resp.IsSuccessStatusCode)
                return Err("GLM", $"API 错误(HTTP {(int)resp.StatusCode})");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.False)
                return Err("GLM", $"API 错误:{Str(root, "msg")}");
            if (!root.TryGetProperty("data", out var data))
                return Err("GLM", "响应中缺少 data 字段");

            var windows = ParseZhipuWindows(data);
            if (windows.Count == 0)
                return Err("GLM", "响应中没有可解析的额度条目");
            return Ok("GLM", windows, Str(data, "level"));
        }
        catch (Exception ex)
        {
            return Err("GLM", $"网络错误:{ex.Message}");
        }
    }

    private static List<QuotaWindow> ParseZhipuWindows(JsonElement data)
    {
        QuotaWindow? fiveHour = null, weekly = null;
        var unclassified = new List<(long? ResetMs, double Pct, DateTime? Reset)>();

        if (data.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in limits.EnumerateArray())
            {
                string type = Str(item, "type");
                if (!type.Equals("TOKENS_LIMIT", StringComparison.OrdinalIgnoreCase) &&
                    !type.Equals("CREDIT_LIMIT", StringComparison.OrdinalIgnoreCase)) continue;

                double pct = item.TryGetProperty("percentage", out var p) ? p.GetDouble() : 0;
                long? resetMs = item.TryGetProperty("nextResetTime", out var r) ? r.GetInt64() : null;
                DateTime? reset = resetMs is > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(resetMs.Value).UtcDateTime : null;
                int unit = item.TryGetProperty("unit", out var u) ? u.GetInt32() : 0;

                if (unit == 3 && fiveHour == null) fiveHour = new QuotaWindow("5h", pct, null, null, reset);
                else if (unit == 6 && weekly == null) weekly = new QuotaWindow("周", pct, null, null, reset);
                else unclassified.Add((resetMs, pct, reset));
            }
        }

        // 兜底:无重置时间的优先归 5h 窗,其余按重置时间升序填缺(unit 缺失或新增取值时)
        foreach (var e in unclassified
            .OrderBy(x => x.ResetMs is null ? 0 : 1)
            .ThenBy(x => x.ResetMs ?? long.MinValue))
        {
            if (fiveHour == null) fiveHour = new QuotaWindow("5h", e.Pct, null, null, e.Reset);
            else if (weekly == null) weekly = new QuotaWindow("周", e.Pct, null, null, e.Reset);
        }

        var result = new List<QuotaWindow>(2);
        if (fiveHour != null) result.Add(fiveHour);
        if (weekly != null) result.Add(weekly);
        return result;
    }

    // ==================== Xiaomi MiMo(控制台 Cookie:余额 + Token Plan) ====================
    // GET https://platform.xiaomimimo.com/api/v1/{balance, tokenPlan/detail, tokenPlan/usage}
    // tp-/sk- API Key 均无法查询,必须小米账号会话 Cookie(api-platform_serviceToken + userId),
    // 从浏览器 DevTools 复制任意 /api/v1 请求的 Cookie 请求头即可。
    // 响应信封 {code, message?, data?}:code=0 成功;401 未登录(附 loginUrl)。
    // balance = 按量付费余额;tokenPlan/usage = 套餐月度用量(百分比);tokenPlan/detail = 套餐代号/周期。

    private const string MimoApiBase = "https://platform.xiaomimimo.com/api/v1";

    private sealed record MimoReply(int Code, string Message, JsonElement? Data, bool HttpAuth);

    public async Task<PlanStatus> QueryMimoAsync(string cookie)
    {
        try
        {
            if (!cookie.Contains("api-platform_serviceToken", StringComparison.OrdinalIgnoreCase)
                || !cookie.Contains("userId", StringComparison.OrdinalIgnoreCase))
                return Err("MiMo", "Cookie 缺少 api-platform_serviceToken / userId,请从控制台 /api/v1 请求头完整复制");

            var balanceTask = MimoGetAsync(cookie, "balance");
            var detailTask = MimoGetAsync(cookie, "tokenPlan/detail");
            var usageTask = MimoGetAsync(cookie, "tokenPlan/usage");
            await Task.WhenAll(balanceTask, detailTask, usageTask);
            var bal = balanceTask.Result;

            if (bal.HttpAuth || bal.Code is 401 or 403)
                return Err("MiMo", "登录失效(Cookie 过期或无效),请重新登录 platform.xiaomimimo.com 复制 Cookie");
            if (bal.Code != 0 || bal.Data is not { } balData)
                return Err("MiMo", $"余额查询失败:{(bal.Message.Length > 0 ? bal.Message : $"code {bal.Code}")}");
            if (!balData.TryGetProperty("balance", out var balVal))
                return Err("MiMo", "余额响应中没有 balance 字段");
            double balance = balVal.ValueKind == JsonValueKind.Number ? balVal.GetDouble()
                : balVal.ValueKind == JsonValueKind.String && double.TryParse(balVal.GetString(), out var bd) ? bd
                : double.NaN;
            if (double.IsNaN(balance))
                return Err("MiMo", "余额数值无法解析");
            string currency = Str(balData, "currency");
            if (currency.Length == 0) currency = "CNY";

            var windows = new List<QuotaWindow>
            {
                new("余额", null, balance, null, null),
            };

            // Token Plan 为可选订阅:detail/usage 任一失败都只影响百分比窗口,不影响余额展示
            DateTime? periodEnd = null;
            string? planCode = null;
            bool expired = false;
            var det = detailTask.Result;
            if (!det.HttpAuth && det.Code == 0 && det.Data is { } detData)
            {
                planCode = Str(detData, "planCode");
                expired = detData.TryGetProperty("expired", out var ex) && ex.ValueKind == JsonValueKind.True;
                periodEnd = ParseMimoPeriodEnd(Str(detData, "currentPeriodEnd"));
            }

            var usage = usageTask.Result;
            if (!usage.HttpAuth && usage.Code == 0 && usage.Data is { } usageData)
                windows.AddRange(ParseMimoUsageWindows(usageData, periodEnd));

            string plan = planCode is { Length: > 0 } pc
                ? (expired ? $"{pc}(已到期)" : pc)
                : "按量付费";
            return Ok("MiMo", windows, plan, currency);
        }
        catch (Exception ex)
        {
            return Err("MiMo", $"网络错误:{ex.Message}");
        }
    }

    /// <summary>GET 控制台 API:带 Cookie 与浏览器同款头;HTTP 3xx/401/403 与信封 code=401 统一视为未登录。</summary>
    private async Task<MimoReply> MimoGetAsync(string cookie, string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{MimoApiBase}/{path}");
        req.Headers.TryAddWithoutValidation("Cookie", cookie);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        req.Headers.TryAddWithoutValidation("Origin", "https://platform.xiaomimimo.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://platform.xiaomimimo.com/");
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36");
        using var resp = await _http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();

        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
            or System.Net.HttpStatusCode.Moved or System.Net.HttpStatusCode.Redirect
            or System.Net.HttpStatusCode.RedirectMethod or System.Net.HttpStatusCode.TemporaryRedirect)
            return new MimoReply(401, "未登录", null, true);

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            int code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : -1;
            string msg = Str(root, "message");
            if (msg.Length == 0) msg = Str(root, "msg");
            JsonElement? data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                ? d.Clone() : null;
            return new MimoReply(code, msg, data, false);
        }
        catch (JsonException)
        {
            return new MimoReply(-1, $"响应非 JSON(HTTP {(int)resp.StatusCode})", null, false);
        }
    }

    /// <summary>tokenPlan/usage → 百分比窗口;data.monthUsage.percent 与 items[] 均防御式匹配。
    /// items 每条一个额度桶(name/percent/used/limit),为空时退回月总百分比。</summary>
    private static List<QuotaWindow> ParseMimoUsageWindows(JsonElement data, DateTime? periodEnd)
    {
        var windows = new List<QuotaWindow>();
        if (!data.TryGetProperty("monthUsage", out var mu) || mu.ValueKind != JsonValueKind.Object)
            return windows;

        if (mu.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                double pct = item.TryGetProperty("percent", out var p) ? ParseD2(p) : double.NaN;
                if (double.IsNaN(pct))
                {
                    double used = ParseD(item, "used"), limit = ParseD(item, "limit");
                    pct = limit > 0 ? used / limit * 100.0 : 0;
                }
                string label = Str(item, "name");
                if (label.Length == 0) label = "月额度";
                windows.Add(new QuotaWindow(label, Math.Clamp(pct, 0, 100), null, null, periodEnd));
            }
        }
        if (windows.Count == 0 && mu.TryGetProperty("percent", out var mp))
            windows.Add(new QuotaWindow("月额度", Math.Clamp(ParseD2(mp), 0, 100), null, null, periodEnd));
        return windows;
    }

    /// <summary>currentPeriodEnd("yyyy-MM-dd HH:mm:ss",控制台周期截止):按 UTC 解析(对齐 CodexBar 实测)。</summary>
    private static DateTime? ParseMimoPeriodEnd(string s) =>
        DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt : null;

    // ==================== OpenCode Go(sk- API Key,官方使用量端点) ====================
    // GET https://opencode.ai/zen/go/v1/usage
    // Authorization: Bearer sk-...(opencode.ai 控制台的 API Key;控制台会话 Cookie 走另一端点,不通用)
    // 响应形态(CodexBar 实测):{ usage: { rolling|weekly|monthly: { percent, resetsAt }, ... } }
    // percent 为 0-100(CodexBar 口径),resetsAt 为 ISO 时间;部分版本可能带 usedDollars/limitDollars(美元限额)。

    private const string OpenCodeUsageUrl = "https://opencode.ai/zen/go/v1/usage";

    public async Task<PlanStatus> QueryOpenCodeAsync(string key)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, OpenCodeUsageUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var resp = await _http.SendAsync(req);
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return Err("OpenCode Go", $"鉴权失败(HTTP {(int)resp.StatusCode}),请检查 API Key(sk-,opencode.ai/console)");
            if (!resp.IsSuccessStatusCode)
                return Err("OpenCode Go", $"API 错误(HTTP {(int)resp.StatusCode})");
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return ParseOpenCodeUsage(doc.RootElement);
        }
        catch (Exception ex)
        {
            return Err("OpenCode Go", $"网络错误:{ex.Message}");
        }
    }

    /// <summary>解析 zen/go/v1/usage 响应多余字段只取认识的部分;percent 0-100,0-1 分数形态防御式换算。</summary>
    internal static PlanStatus ParseOpenCodeUsage(JsonElement root)
    {
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            return Err("OpenCode Go", $"API 错误:{Str(err, "message")}");
        if (!(root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object))
            return Err("OpenCode Go", "响应中没有 usage 字段");

        // 逐窗口取节点;percent 在窗口节点内。0-1 分数形态判定:全组 ≤1 且有非零值时 ×100
        var wins = new[] { TryWin(usage, "rolling"), TryWin(usage, "weekly"), TryWin(usage, "monthly") };
        var raws = wins.Select(w => w.Ok ? TryNum(w.Node, "percent", "usagePercent", "usedPercent") : 0.0).ToArray();
        bool fraction = raws.All(v => v >= 0 && v <= 1) && raws.Any(v => v > 0);

        var windows = new List<QuotaWindow>();
        string[] labels = { "5h", "周", "月" };
        for (int i = 0; i < wins.Length; i++)
        {
            var w = wins[i];
            if (!w.Ok) continue;
            double pct = fraction ? raws[i] * 100.0 : raws[i];
            if (pct == 0 && w.Ok)
            {
                double usedD = TryNum(w.Node, "usedDollars", "usageDollars"),
                       limitD = TryNum(w.Node, "limitDollars", "limit");
                if (limitD > 0) pct = Math.Clamp(usedD / limitD * 100.0, 0, 100);
            }
            windows.Add(MakeOpenCodeWindow(labels[i], Math.Clamp(pct, 0, 100), w.Reset, w.Node));
        }
        if (windows.Count == 0)
            return Err("OpenCode Go", "响应中没有可解析的额度窗口(rolling/weekly/monthly)");

        // 顶层可能带 plan:go / go-plus → 展示名
        string plan = Str(root, "plan");
        if (plan.Length == 0) plan = Str(usage, "plan");
        string planName = plan.ToLowerInvariant() switch
        {
            var s when s.Contains("plus") => "Go Plus",
            var s when s.Contains("go") => "Go",
            { Length: > 0 } s => s,
            _ => "Go",
        };
        return Ok("OpenCode Go", windows, planName, null);
    }

    /// <summary>单窗口:(名, 重置时间, 节点);节点缺失 = 该窗口未订阅,返回 false 跳过。</summary>
    private static (bool Ok, DateTime? Reset, JsonElement Node) TryWin(JsonElement usage, string name)
    {
        if (!(usage.TryGetProperty(name, out var win) &&
              win.ValueKind is JsonValueKind.Object))
            return (false, DateTime.MinValue, default);
        DateTime? reset = TryReset(win, "resetsAt", "resetAt", "reset_at");
        if (reset == null)
        {
            double sec = TryNum(win, "resetInSeconds", "resets_in_seconds");
            if (sec > 0) reset = DateTime.UtcNow.AddSeconds(sec);
        }
        return (true, reset, win.Clone());
    }

    /// <summary>百分比窗口;若响应带 usedDollars/limitDollars(美元限额)则一并记入绝对值。</summary>
    private static QuotaWindow MakeOpenCodeWindow(string label, double pct, DateTime? reset, JsonElement node)
    {
        double usedD = TryNum(node, "usedDollars", "usageDollars"),
               limitD = TryNum(node, "limitDollars", "limit");
        if (limitD > 0)
            return new QuotaWindow(label, pct, Math.Max(0, limitD - usedD), limitD, reset);
        return new QuotaWindow(label, pct, null, null, reset);
    }

    /// <summary>按候选字段名取第一个数值;不存在返回 0(与 ParseD 一致,仅在兜底语境使用)。</summary>
    private static double TryNum(JsonElement obj, params string[] props)
    {
        foreach (var p in props)
            if (obj.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number)
                return v.GetDouble();
        return 0;
    }

    /// <summary>按候选字段名取 ISO 重置时间。</summary>
    private static DateTime? TryReset(JsonElement obj, params string[] props)
    {
        foreach (var p in props)
            if (obj.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var dt = ParseResetTime(v);
                if (dt != null) return dt;
            }
        return null;
    }

    // ==================== 火山方舟 Agent/Coding Plan(非官方) ====================
    // 控制面 OpenAPI(非推理域名),需火山引擎 AK/SK 签名 V4(推理 API Key 无法使用):
    // POST https://open.volcengineapi.com/?Action=GetAFPUsage&Region=cn-beijing&Version=2024-01-01
    // 先查 Agent Plan(GetAFPUsage,绝对额度),无订阅再查 Coding Plan(GetCodingPlanUsage,百分比)。

    private const string VolcHost = "open.volcengineapi.com";
    private const string VolcVersion = "2024-01-01";
    private const string VolcRegion = "cn-beijing";
    private const string VolcService = "ark";
    private const string VolcContentType = "application/json; charset=utf-8";
    private const string VolcSignedHeaders = "host;x-date;x-content-sha256;content-type";
    private const string VolcAkskHint = "需填火山引擎控制台的 AccessKey ID/Secret(不是推理 API Key),且账号需有 Ark 用量查询权限";

    public async Task<PlanStatus> QueryVolcAsync(string ak, string sk)
    {
        // 1) Agent Plan:绝对额度 Quota/Used
        var afp = await VolcCallAsync(ak, sk, "GetAFPUsage");
        if (afp.Status is VolcStatus.Auth) return Err("火山方舟", $"{afp.Message}。{VolcAkskHint}");
        if (afp.Status is VolcStatus.Body)
        {
            var inner = afp.Body.TryGetProperty("Result", out var r) ? r : afp.Body;
            var windows = ParseAfpTiers(inner);
            if (windows.Count > 0)
            {
                string plan = Str(inner, "PlanType");
                return Ok("火山方舟", windows, plan.Length > 0 ? $"Agent Plan {plan}" : "Agent Plan", "点");
            }
        }

        // 2) Coding Plan:百分比
        var cp = await VolcCallAsync(ak, sk, "GetCodingPlanUsage");
        if (cp.Status is VolcStatus.Auth) return Err("火山方舟", $"{cp.Message}。{VolcAkskHint}");
        if (cp.Status is VolcStatus.Body)
        {
            var inner = cp.Body.TryGetProperty("Result", out var r2) ? r2 : cp.Body;
            var windows = ParseCodingPlanTiers(inner);
            if (windows.Count > 0)
                return Ok("火山方舟", windows, "Coding Plan");
        }

        string? detail = FirstError(afp, cp);
        return Err("火山方舟", detail ?? "未找到生效中的 Agent Plan / Coding Plan 订阅");
    }

    private static string? FirstError(VolcResponse a, VolcResponse b)
    {
        var sb = new List<string>();
        if (a.Status is VolcStatus.Soft && a.Message != null) sb.Add($"GetAFPUsage:{a.Message}");
        if (b.Status is VolcStatus.Soft && b.Message != null) sb.Add($"GetCodingPlanUsage:{b.Message}");
        return sb.Count > 0 ? string.Join("; ", sb) : null;
    }

    /// <summary>GetAFPUsage 的 5h/周/月窗口;Quota&lt;=0 视为未订阅该窗口,跳过。</summary>
    private static List<QuotaWindow> ParseAfpTiers(JsonElement result)
    {
        var windows = new List<QuotaWindow>();
        foreach (var (key, label) in new[] { ("AFPFiveHour", "5h"), ("AFPWeekly", "周"), ("AFPMonthly", "月") })
        {
            if (!result.TryGetProperty(key, out var win)) continue;
            double quota = ParseD(win, "Quota");
            if (quota <= 0) continue;
            double used = ParseD(win, "Used");
            DateTime? reset = win.TryGetProperty("ResetTime", out var rt) ? ParseResetTime(rt) : null;
            windows.Add(new QuotaWindow(label, used / quota * 100.0, quota - used, quota, reset));
        }
        return windows;
    }

    /// <summary>GetCodingPlanUsage 的 session/weekly/monthly 窗口(只有百分比,防御式字段匹配)。</summary>
    private static List<QuotaWindow> ParseCodingPlanTiers(JsonElement result)
    {
        var windows = new List<QuotaWindow>();
        if (!(result.TryGetProperty("QuotaUsage", out var arr) && arr.ValueKind == JsonValueKind.Array)
            && !(result.TryGetProperty("Usages", out arr) && arr.ValueKind == JsonValueKind.Array)
            && !(result.TryGetProperty("Details", out arr) && arr.ValueKind == JsonValueKind.Array))
            return windows;

        foreach (var item in arr.EnumerateArray())
        {
            string label = Str(item, "Level");
            if (label.Length == 0) label = Str(item, "Type");
            string? name = label.ToLowerInvariant() switch
            {
                "session" or "5h" or "fivehour" or "five_hour" or "rolling_5h" => "5h",
                "weekly" or "week" or "7d" => "周",
                "monthly" or "month" => "月",
                _ => null,
            };
            if (name == null) continue;

            double used = item.TryGetProperty("Percent", out var p) ? ParseD2(p) : 0;
            // 字段名双兜底(对齐 CC Switch 实测):ResetTime / ResetTimestamp
            DateTime? reset = null;
            if (item.TryGetProperty("ResetTime", out var rt)) reset = ParseResetTime(rt);
            else if (item.TryGetProperty("ResetTimestamp", out var rts)) reset = ParseResetTime(rts);
            windows.Add(new QuotaWindow(name, used, null, null, reset));
        }
        return windows;
    }

    private enum VolcStatus { Body, Auth, Soft }

    private sealed record VolcResponse(VolcStatus Status, JsonElement Body = default, string? Message = null);

    private async Task<VolcResponse> VolcCallAsync(string ak, string sk, string action)
    {
        try
        {
            // canonical query 按 key 字母序(Action < Region < Version),签名与实际 URL 共用同一份串
            string query = $"Action={action}&Region={VolcRegion}&Version={VolcVersion}";
            (string authorization, string xDate, string payloadHash) = VolcSign(ak, sk, query, "");

            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{VolcHost}/?{query}");
            req.Headers.TryAddWithoutValidation("X-Date", xDate);
            req.Headers.TryAddWithoutValidation("X-Content-Sha256", payloadHash);
            req.Headers.TryAddWithoutValidation("Authorization", authorization);
            req.Content = new StringContent("", new UTF8Encoding(false), "application/json"); // charset=utf-8 与签名一致

            using var resp = await _http.SendAsync(req);
            string body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            // 火山网关常以 200/4xx + ResponseMetadata.Error 信封返回业务错误
            if (TryVolcError(doc.RootElement, out var code, out var msg))
            {
                if (IsVolcAuthCode(code))
                    return new VolcResponse(VolcStatus.Auth, Message: $"鉴权失败({code}):{msg}");
                return new VolcResponse(VolcStatus.Soft, Message: $"API 错误({code}):{msg}");
            }
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new VolcResponse(VolcStatus.Auth, Message: $"鉴权失败(HTTP {(int)resp.StatusCode})");
            if (!resp.IsSuccessStatusCode)
                return new VolcResponse(VolcStatus.Soft, Message: $"API 错误(HTTP {(int)resp.StatusCode})");

            var bodyElement = doc.RootElement.Clone();
            DumpVolcBody(action, bodyElement); // 诊断:非官方接口,落盘真实响应便于核对字段
            return new VolcResponse(VolcStatus.Body, bodyElement);
        }
        catch (Exception ex)
        {
            return new VolcResponse(VolcStatus.Soft, Message: $"网络错误:{ex.Message}");
        }
    }

    /// <summary>火山引擎签名 V4(AWS SigV4 变体):canonical headers 固定顺序不按字母序,
    /// algorithm 为 HMAC-SHA256(无 AWS4 前缀),scope 结尾 request,kDate=HMAC(SK, date)。</summary>
    internal static (string Authorization, string XDate, string PayloadHash) VolcSign(
        string ak, string sk, string canonicalQuery, string payload, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        string xDate = now.ToString("yyyyMMddTHHmmssZ");
        string shortDate = now.ToString("yyyyMMdd");
        string payloadHash = Sha256Hex(payload);

        string canonicalHeaders =
            $"host:{VolcHost}\nx-date:{xDate}\nx-content-sha256:{payloadHash}\ncontent-type:{VolcContentType}\n";
        string canonicalRequest =
            $"POST\n/\n{canonicalQuery}\n{canonicalHeaders}\n{VolcSignedHeaders}\n{payloadHash}";

        string scope = $"{shortDate}/{VolcRegion}/{VolcService}/request";
        string stringToSign = $"HMAC-SHA256\n{xDate}\n{scope}\n{Sha256Hex(canonicalRequest)}";

        byte[] kDate = Hmac(Encoding.UTF8.GetBytes(sk), shortDate);
        byte[] kRegion = Hmac(kDate, VolcRegion);
        byte[] kService = Hmac(kRegion, VolcService);
        byte[] kSigning = Hmac(kService, "request");
        string signature = Hex(Hmac(kSigning, stringToSign));

        string authorization =
            $"HMAC-SHA256 Credential={ak}/{scope}, SignedHeaders={VolcSignedHeaders}, Signature={signature}";
        return (authorization, xDate, payloadHash);
    }

    private static bool TryVolcError(JsonElement body, out string code, out string msg)
    {
        code = "";
        msg = "";
        if (!(body.TryGetProperty("ResponseMetadata", out var meta) && meta.TryGetProperty("Error", out var err))
            && !body.TryGetProperty("Error", out err))
            return false;
        code = Str(err, "Code");
        msg = Str(err, "Message");
        return code.Length > 0 || msg.Length > 0;
    }

    private static readonly object _dumpLock = new();

    /// <summary>把火山 OpenAPI 最近一次成功响应落盘到 exe 旁 volc-last-response.json
    /// (结构:{updatedAt, GetAFPUsage, GetCodingPlanUsage})。非官方接口字段若有变动,
    /// 可据此核对真实字段名。失败静默,不影响查询。</summary>
    private static void DumpVolcBody(string action, JsonElement body)
    {
        try
        {
            lock (_dumpLock)
            {
                string path = Path.Combine(AppContext.BaseDirectory, "volc-last-response.json");
                var map = new Dictionary<string, JsonElement>();
                if (File.Exists(path))
                {
                    try
                    {
                        using var old = JsonDocument.Parse(File.ReadAllText(path));
                        if (old.RootElement.ValueKind == JsonValueKind.Object)
                            foreach (var p in old.RootElement.EnumerateObject())
                                if (p.Name != "updatedAt")
                                    map[p.Name] = p.Value.Clone();
                    }
                    catch { }
                }
                map[action] = body;
                map["updatedAt"] = JsonSerializer.SerializeToElement(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                File.WriteAllText(path,
                    JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch { }
    }

    private static bool IsVolcAuthCode(string code)
    {
        string c = code.ToLowerInvariant();
        return c.Contains("auth") || c.Contains("signature") || c.Contains("denied")
            || c.Contains("unauthorized") || c.Contains("forbidden") || c.Contains("credential") || c.Contains("token");
    }

    // ==================== 工具 ====================

    /// <summary>解析重置时间:兼容 ISO 字符串与秒/毫秒时间戳;0/负值视为无重置时间。</summary>
    private static DateTime? ParseResetTime(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(v.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToUniversalTime();
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
        {
            if (n <= 0) return null;
            long ms = n < 1_000_000_000_000 ? n * 1000 : n; // 秒级 < 1e12,毫秒 >= 1e12
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        }
        return null;
    }

    private static string Str(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double ParseD(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) ? ParseD2(v) : 0;

    /// <summary>数字/字符串双兼容(如 100 与 "100")。</summary>
    private static double ParseD2(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number ? v.GetDouble()
        : v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out var d) ? d : 0;

    private static byte[] Hmac(byte[] key, string data)
    {
        using var h = new HMACSHA256(key);
        return h.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string Sha256Hex(string data) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();
}
