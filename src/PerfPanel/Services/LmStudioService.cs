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
    public DateTime LastActivityUtc { get; init; }
}

/// <summary>LM Studio 快照:不可变,后台轮询整体换新,UI 线程直接读。</summary>
public sealed record LmStudioSnapshot
{
    public bool Online { get; init; }               // 服务端口可连
    public bool LogAvailable { get; init; }         // 服务日志可读
    public int TotalInFlight { get; init; }
    public IReadOnlyList<LmModelActivity> Models { get; init; } = [];
    public double? LastPrefillTps { get; init; }    // 最近一次 prefill(prompt eval)tok/s
    public double? LastDecodeTps { get; init; }     // 最近一次 decode(eval)tok/s
    public DateTime LastEventUtc { get; init; }
}

/// <summary>
/// LM Studio 本地服务监控。数据全部来自本机服务日志 %USERPROFILE%\.lmstudio\server-logs(免鉴权:
/// 新版 LM Studio 的 REST 接口要求 Bearer token 且 token 明文不落盘,日志路径本地可读且字段更全),
/// 端口 TCP 探测判在线。
/// 解析口径:「Running chat completion」+1、「Finished streaming response」/「Generated prediction」-1
/// 配平并发数;引擎 print_timing 行的 prompt eval = prefill 速度、eval = decode 速度(tokens per second);
/// 「Unloading model … due to TTL」卸载事件立即把模型移出加载集。加载中模型 = 在飞请求 &gt; 0,
/// 或近期有活动(窗口略大于 JIT 默认 5 分钟 TTL)。
/// 日志按天分文件(YYYY-MM/YYYY-MM-DD.N.log),首见文件整读一遍恢复状态,此后按字节偏移增量尾读。
/// </summary>
public sealed class LmStudioService
{
    public static TimeSpan PollInterval => TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LoadedTtl = TimeSpan.FromMinutes(6);     // 略大于 JIT 默认 5 分钟 TTL
    private static readonly TimeSpan StuckTimeout = TimeSpan.FromMinutes(60); // 丢结束行的在飞请求兜底清除

    public LmStudioSnapshot Current { get; private set; } = new();

    private readonly Dictionary<string, List<DateTime>> _inflight = new();                    // 模型 → 在飞请求开始时刻(FIFO)
    private readonly Dictionary<string, (double Progress, DateTime AtUtc)> _progress = new(); // 模型 → 最近一次预填充进度
    private readonly Dictionary<string, DateTime> _lastActivity = new();
    private double? _lastPrefillTps, _lastDecodeTps;
    private DateTime _lastEventUtc = DateTime.MinValue;
    private string? _logFile;
    private long _offset;
    private bool _logAvailable;

    private static readonly Regex ReTps = new(@"([\d.]+)\s*tokens per second", RegexOptions.Compiled);
    private static readonly Regex ReEvent = new(@"^\[[^\]]+\]\[(?:INFO|WARN|ERROR)\](?:\[([^\]]+)\])?\s?(.*)$", RegexOptions.Compiled);
    private static readonly Regex ReProgress = new(@"Prompt processing progress:\s*([\d.]+)%", RegexOptions.Compiled);
    private static readonly Regex ReUnload = new(@"^Unloading model\s+(\S+)", RegexOptions.Compiled);

    public async Task PollAsync()
    {
        bool online = await ProbeAsync(Config.Current.LmStudioPort);
        PollLog();
        var utc = DateTime.UtcNow;
        var models = new List<LmModelActivity>();
        foreach (var id in _lastActivity.Keys.ToList())
        {
            int inflight = CleanInflight(id, utc);
            if (inflight == 0 && utc - _lastActivity[id] > LoadedTtl)
            {
                _lastActivity.Remove(id);   // 活动窗口外(对应 JIT TTL 到期)自然移出
                _progress.Remove(id);
                continue;
            }
            double? prog = null;
            if (_progress.TryGetValue(id, out var pr))
            {
                if (utc - pr.AtUtc <= TimeSpan.FromSeconds(90)) prog = pr.Progress;
                else _progress.Remove(id);
            }
            models.Add(new LmModelActivity
            {
                Id = id, InFlight = inflight, PrefillProgress = prog, LastActivityUtc = _lastActivity[id],
            });
        }
        Current = new LmStudioSnapshot
        {
            Online = online,
            LogAvailable = _logAvailable,
            TotalInFlight = models.Sum(m => m.InFlight),
            Models = models.OrderByDescending(m => m.LastActivityUtc).ToList(),
            LastPrefillTps = _lastPrefillTps,
            LastDecodeTps = _lastDecodeTps,
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
        // 引擎计时行(print_timing,可能不带 [ts][LEVEL] 前缀):prompt eval = prefill,eval = decode
        if (line.Contains("prompt eval time ="))
        {
            if (ReTps.Match(line) is { Success: true } m) { _lastPrefillTps = ParseTps(m); _lastEventUtc = DateTime.UtcNow; }
            return;
        }
        if (line.Contains("eval time ="))
        {
            if (ReTps.Match(line) is { Success: true } m) { _lastDecodeTps = ParseTps(m); _lastEventUtc = DateTime.UtcNow; }
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
            if (_inflight.TryGetValue(id, out var q) && q.Count > 0) q.RemoveAt(0);   // FIFO 配平
            _lastActivity[id] = DateTime.UtcNow;
        }
        else if (id.Length > 0 && ReProgress.Match(msg) is { Success: true } pm)
        {
            if (double.TryParse(pm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                _progress[id] = (pct, DateTime.UtcNow);
            _lastActivity[id] = DateTime.UtcNow;
        }
        else if (msg.StartsWith("Unloading model ") && ReUnload.Match(msg) is { Success: true } um)
        {
            string unloaded = um.Groups[1].Value;
            _lastActivity.Remove(unloaded);   // TTL/手动卸载:立即移出加载集
            _inflight.Remove(unloaded);
            _progress.Remove(unloaded);
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
}
