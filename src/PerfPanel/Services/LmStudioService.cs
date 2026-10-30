using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace PerfPanel.Services;

/// <summary>单个模型的实时活动(从服务日志推断)。</summary>
public sealed record LmModelActivity
{
    public required string Id { get; init; }
    public int InFlight { get; init; }
    public double? PrefillProgress { get; init; }   // 最近一次「Prompt processing progress」%;非 0~100 = 不在预填充
    public LmTps? Tps { get; init; }                // 该模型最近一次计时块(见 LmTps),null = 从未测得
    public DateTime LastActivityUtc { get; init; }
}

/// <summary>
/// 某模型最近一次计时块的速度。计时行只带槽位号不带模型名,归属规则 = 把计时块记给日志里
/// 下一个「Generated prediction」/「Finished streaming response」完成事件的模型
/// (计时块在请求结束时打印,紧跟其完成事件,单模型工作时精确;多模型并发解码时偶有归属偏差)。
/// </summary>
public sealed record LmTps(double? Prefill, double? Decode, DateTime AtUtc);

/// <summary>LM Studio 快照:不可变,后台轮询整体换新,UI 线程直接读。</summary>
public sealed record LmStudioSnapshot
{
    public bool Online { get; init; }               // 服务端口可连
    public bool LogAvailable { get; init; }         // 服务日志可读
    public int TotalInFlight { get; init; }
    public IReadOnlyList<LmModelActivity> Models { get; init; } = [];
    public DateTime LastEventUtc { get; init; }
}

/// <summary>
/// LM Studio 本地服务监控。数据全部来自本机服务日志 %USERPROFILE%\.lmstudio\server-logs(免鉴权:
/// 新版 LM Studio 的 REST 接口要求 Bearer token 且 token 明文不落盘,日志路径本地可读且字段更全),
/// 端口 TCP 探测判在线。
/// 解析口径:「Running chat completion」+1、「Finished streaming response」/「Generated prediction」-1
/// 配平并发数;引擎 print_timing 行的 prompt eval = prefill 速度、eval = decode 速度(tokens per second),
/// 计时块缓存后归属给下一个完成事件的模型(LmTps),值随日志时间戳(计时行首行自带 [yyyy-MM-dd HH:mm:ss],
/// 续行沿用上一条的时间)一起记录,超过 TpsFreshWindow 就视为过期——UI 据此不再显示陈旧速度;
/// 流式请求另有逐 token 的「Accumulated N tokens」行(带模型名),按秒聚合出逐秒 decode 速度,
/// 是 decode 数字实时变动的主要来源;tg_3s 周期行与请求结束行兜底;
/// 计时行只在请求结束时打印一次,长解码中途没有新数据时沿用该模型最近一次测得值;
/// 「Unloading model … due to TTL」卸载事件立即把模型移出加载集。加载中模型 = 在飞请求 &gt; 0,
/// 或近期有活动(窗口略大于 JIT 默认 5 分钟 TTL)。
/// 日志按天分文件(YYYY-MM/YYYY-MM-DD.N.log),首见文件整读一遍恢复状态,此后按字节偏移增量尾读。
/// </summary>
public sealed class LmStudioService
{
    public static TimeSpan PollInterval => TimeSpan.FromSeconds(2);
    public static TimeSpan TpsFreshWindow => TimeSpan.FromSeconds(3);         // 速度值超过该窗口视为过期(UI 不再显示)
    private static readonly TimeSpan PendingTtl = TimeSpan.FromSeconds(5);    // 计时块没等到归属事件就丢弃
    private static readonly TimeSpan LoadedTtl = TimeSpan.FromMinutes(6);     // 略大于 JIT 默认 5 分钟 TTL
    private static readonly TimeSpan StuckTimeout = TimeSpan.FromMinutes(60); // 丢结束行的在飞请求兜底清除

    public LmStudioSnapshot Current { get; private set; } = new();

    private readonly Dictionary<string, List<DateTime>> _inflight = new();                    // 模型 → 在飞请求开始时刻(FIFO)
    private readonly Dictionary<string, (double Progress, DateTime AtUtc)> _progress = new(); // 模型 → 最近一次预填充进度
    private readonly Dictionary<string, DateTime> _lastActivity = new();
    private readonly Dictionary<string, LmTps> _modelTps = new();                             // 模型 → 最近一次计时块速度
    private readonly Dictionary<string, (int Counter, DateTime SecUtc, int Tokens)> _acc = new(); // 模型 → 流式逐 token 计数窗口
    private LmTps? _pendingTps;                    // 刚解析到的计时块,等下一个完成事件归属
    private DateTime _lastEventUtc = DateTime.MinValue;
    private DateTime _lastLogTsUtc = DateTime.MinValue;   // 最近一条带 [yyyy-MM-dd HH:mm:ss] 前缀日志行的时间
    private string? _logFile;
    private long _offset;
    private bool _logAvailable;

    private static readonly Regex ReTps = new(@"([\d.]+)\s*tokens per second", RegexOptions.Compiled);
    private static readonly Regex ReLiveTps3s = new(@"tg_3s\s*=\s*([\d.]+)\s*t/s", RegexOptions.Compiled);
    private static readonly Regex ReLiveTpsGen = new(@"\btg\s*=\s*([\d.]+)\s*t/s", RegexOptions.Compiled);
    private static readonly Regex ReAcc = new(@"^Accumulated\s+(\d+)\s+tokens", RegexOptions.Compiled);
    private static readonly Regex ReEvent = new(@"^\[[^\]]+\]\[(?:INFO|WARN|ERROR)\](?:\[([^\]]+)\])?\s?(.*)$", RegexOptions.Compiled);
    private static readonly Regex ReProgress = new(@"Prompt processing progress:\s*([\d.]+)%", RegexOptions.Compiled);
    private static readonly Regex ReUnload = new(@"^Unloading model\s+(\S+)", RegexOptions.Compiled);
    private static readonly Regex ReLogTs = new(@"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]", RegexOptions.Compiled);

    public async Task PollAsync()
    {
        bool online = await ProbeAsync(Config.Current.LmStudioPort);
        PollLog();
        var utc = DateTime.UtcNow;
        if (_pendingTps is { } pend && utc - pend.AtUtc > PendingTtl)
            _pendingTps = null;   // 计时块没等到归属事件(如请求异常终止),丢弃
        var models = new List<LmModelActivity>();
        foreach (var id in _lastActivity.Keys.ToList())
        {
            int inflight = CleanInflight(id, utc);
            if (inflight == 0 && utc - _lastActivity[id] > LoadedTtl)
            {
                _lastActivity.Remove(id);   // 活动窗口外(对应 JIT TTL 到期)自然移出
                _progress.Remove(id);
                _modelTps.Remove(id);
                _acc.Remove(id);
                continue;
            }
            double? prog = null;
            if (_progress.TryGetValue(id, out var pr))
            {
                if (utc - pr.AtUtc <= TimeSpan.FromSeconds(90)) prog = pr.Progress;
                else _progress.Remove(id);
            }
            _modelTps.TryGetValue(id, out var tps);
            models.Add(new LmModelActivity
            {
                Id = id, InFlight = inflight, PrefillProgress = prog, Tps = tps, LastActivityUtc = _lastActivity[id],
            });
        }
        Current = new LmStudioSnapshot
        {
            Online = online,
            LogAvailable = _logAvailable,
            TotalInFlight = models.Sum(m => m.InFlight),
            Models = models.OrderByDescending(m => m.LastActivityUtc).ToList(),
            LastEventUtc = _lastEventUtc,
        };
    }

    /// <summary>端口可连 = 服务在跑。探测用 401 也算在线(无 token 也拿得到这个信号)。</summary>
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

    // ---------- 服务日志尾读 ----------

    private static string? ResolveLogDir()
    {
        var custom = Config.Current.LmStudioLogDir;
        if (custom.Length > 0) return Directory.Exists(custom) ? custom : null;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "server-logs");
        return Directory.Exists(dir) ? dir : null;
    }

    private static string? FindLatestLogFile(string dir)
    {
        string? latest = null;
        foreach (var f in Directory.EnumerateFiles(dir, "*.log", SearchOption.AllDirectories))
            if (latest == null || string.CompareOrdinal(f, latest) > 0) latest = f;
        return latest;   // 相对路径 YYYY-MM/YYYY-MM-DD.N.log 字典序 = 时间序
    }

    private void PollLog()
    {
        var dir = ResolveLogDir();
        var latest = dir is null ? null : FindLatestLogFile(dir);
        if (latest is null)
        {
            _logFile = null; _offset = 0; _logAvailable = false;
            return;
        }
        try
        {
            using var fs = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!string.Equals(latest, _logFile, StringComparison.OrdinalIgnoreCase) || fs.Length < _offset)
            {
                _logFile = latest;
                _offset = 0;    // 首见/轮转/重建:整个文件解析一遍,恢复在飞请求与上次速度
            }
            if (fs.Length <= _offset) { _logAvailable = true; return; }

            fs.Seek(_offset, SeekOrigin.Begin);
            var buf = new byte[fs.Length - _offset];
            int read = 0;
            while (read < buf.Length)
            {
                int n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            int cut = Array.LastIndexOf(buf, (byte)'\n');
            if (cut < 0) return;    // 尾部是不完整行,留给下次
            _offset += cut + 1;
            _logAvailable = true;
            // 只处理到最后一个换行:行边界是单字节 '\n',UTF-8 多字节字符不会被切开
            foreach (var raw in Encoding.UTF8.GetString(buf, 0, cut + 1).Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length > 0) ProcessLine(line);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { _logAvailable = false; }
    }

    private void ProcessLine(string line)
    {
        var lineTs = ParseLogTs(line);
        if (lineTs is { } t) _lastLogTsUtc = t;
        // 计时行首行自带日志时间戳,续行(如 eval 行)不带——沿用上一条带时间戳行的时间;异常日志才退回解析时刻
        var at = lineTs ?? (_lastLogTsUtc > DateTime.MinValue ? _lastLogTsUtc : DateTime.UtcNow);

        // 解码中的周期性计时(timings-per-token,约每 200 token 一条):「n_gen = N, tg = X t/s, tg_3s = Y t/s」,
        // tg_3s = 最近 3 秒滚动生成速度,是唯一能让 decode 数字实时动的来源(请求结束行每 7s+ 才一条)。
        // 行内无模型名:仅当只有一个模型在飞时归属给它,多模型并发解码时跳过(宁可沿用旧值不错记)。
        if (line.Contains("n_gen ="))
        {
            var m = ReLiveTps3s.Match(line) is { Success: true } m3 ? m3 : ReLiveTpsGen.Match(line);
            if (m.Success)
            {
                var busyModels = _inflight.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToList();
                if (busyModels.Count == 1)
                {
                    _modelTps.TryGetValue(busyModels[0], out var prev);
                    _modelTps[busyModels[0]] = new LmTps(prev?.Prefill, ParseTps(m), at);
                }
                _lastEventUtc = DateTime.UtcNow;
            }
            return;
        }

        // 引擎计时行(print_timing,可能不带 [ts][LEVEL] 前缀):prompt eval = prefill,eval = decode。
        // 块先入 _pendingTps,等下一个完成事件归属到具体模型(计时行不带模型名)。
        if (line.Contains("prompt eval time ="))
        {
            if (ReTps.Match(line) is { Success: true } m)
            {
                var cur = _pendingTps;
                _pendingTps = new LmTps(ParseTps(m), cur?.Decode, at);
                _lastEventUtc = DateTime.UtcNow;
            }
            return;
        }
        if (line.Contains("eval time ="))
        {
            if (ReTps.Match(line) is { Success: true } m)
            {
                var cur = _pendingTps;
                _pendingTps = new LmTps(cur?.Prefill, ParseTps(m), at);
                _lastEventUtc = DateTime.UtcNow;
            }
            return;
        }

        var ev = ReEvent.Match(line);
        if (!ev.Success) return;
        string id = ev.Groups[1].Value, msg = ev.Groups[2].Value;

        if (id.Length > 0 && msg.StartsWith("Running "))
        {
            if (!_inflight.TryGetValue(id, out var q)) _inflight[id] = q = new List<DateTime>();
            q.Add(DateTime.UtcNow);
            _lastActivity[id] = DateTime.UtcNow;
        }
        else if (id.Length > 0 &&
                 (msg.StartsWith("Finished streaming response") || msg.StartsWith("Generated prediction")))
        {
            if (_pendingTps is { } pend)
            {
                _modelTps[id] = pend;   // 计时块紧跟其完成事件,归属给该模型
                _pendingTps = null;
            }
            if (_inflight.TryGetValue(id, out var q) && q.Count > 0) q.RemoveAt(0);   // FIFO 配平
            _lastActivity[id] = DateTime.UtcNow;
        }
        else if (id.Length > 0 && ReProgress.Match(msg) is { Success: true } pm)
        {
            if (double.TryParse(pm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                _progress[id] = (pct, DateTime.UtcNow);
            _lastActivity[id] = DateTime.UtcNow;
        }
        else if (id.Length > 0 && ReAcc.Match(msg) is { Success: true } am)
        {
            // 流式逐 token 行「Accumulated N tokens …」(行内自带模型名,归属精确):按日志秒聚合 token 增量,
            // 秒界跨越时用 窗口token数/秒数 得到逐秒 decode 速度——比 tg_3s(每~200 token 一条)更实时。
            if (int.TryParse(am.Groups[1].Value, out var cnt))
            {
                var s = at;
                if (!_acc.TryGetValue(id, out var st) || cnt <= st.Counter || (s - st.SecUtc).TotalSeconds > 5)
                {
                    _acc[id] = (cnt, s, 0);   // 新请求(计数回退)或断流过久:重开窗口,本行不产出速度
                }
                else if (s > st.SecUtc)
                {
                    double rate = st.Tokens / (s - st.SecUtc).TotalSeconds;
                    if (rate is > 0 and <= 2000)
                        _modelTps[id] = new LmTps(_modelTps.TryGetValue(id, out var prev) ? prev.Prefill : null, rate, at);
                    _acc[id] = (cnt, s, cnt - st.Counter);
                }
                else
                {
                    _acc[id] = (cnt, st.SecUtc, st.Tokens + cnt - st.Counter);
                }
                _lastActivity[id] = DateTime.UtcNow;   // 流式输出本身证明模型在跑
                _lastEventUtc = DateTime.UtcNow;
            }
        }
        else if (msg.StartsWith("Unloading model ") && ReUnload.Match(msg) is { Success: true } um)
        {
            string unloaded = um.Groups[1].Value;
            _lastActivity.Remove(unloaded);   // TTL/手动卸载:立即移出加载集
            _inflight.Remove(unloaded);
            _progress.Remove(unloaded);
            _modelTps.Remove(unloaded);
            _acc.Remove(unloaded);
        }
    }

    private int CleanInflight(string id, DateTime utc)
    {
        if (!_inflight.TryGetValue(id, out var q)) return 0;
        q.RemoveAll(t => utc - t > StuckTimeout);
        return q.Count;
    }

    private static double? ParseTps(Match m) =>
        double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>解析日志行首的 [yyyy-MM-dd HH:mm:ss](LM Studio 本地时区)为 UTC;无前缀返回 null。</summary>
    private static DateTime? ParseLogTs(string line)
    {
        var m = ReLogTs.Match(line);
        if (!m.Success) return null;
        return DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            ? local.ToUniversalTime()
            : null;
    }
}
