using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace PerfPanel.Services;

/// <summary>GitHub Releases 更新检测:启动 5s 后查一次,此后每 6 小时一次;
/// 最新 release tag(v2026.11.11 日期式口径)与程序集 Version 比较,发现新版本时回调 UI。
/// GitHub 不可达/超限静默跳过,绝不影响面板本体;同一 tag 只提示一次(结果缓存内存)。</summary>
public sealed class UpdateCheckService
{
    private const string ReleasesApi = "https://api.github.com/repos/YZz-S/PerfPanel/releases/latest";
    private const string ReleasesPage = "https://github.com/YZz-S/PerfPanel/releases";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    public string? LatestTag { get; private set; }
    public string LatestUrl { get; private set; } = ReleasesPage;

    /// <summary>有比当前程序集版本更新的 release(tag 大于 csproj Version,日期式比较)。</summary>
    public bool UpdateAvailable => LatestTag is { } tag && IsNewer(tag, CurrentVersion());

    /// <summary>当前程序集版本(csproj Version 属性,如 2026.11.11)。</summary>
    public static string CurrentVersion() =>
        (Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0)).ToString(3);

    /// <summary>tag 形如 v2026.11.11(日期式,前缀 v/V);解析失败视为不比较,不误报。</summary>
    public static bool IsNewer(string tag, string current)
    {
        try
        {
            return Version.Parse(tag.TrimStart('v', 'V')) > Version.Parse(current);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>检测循环(启动时 Task 拉起):onNewVersion(tag, url) 仅在有新版本时触发一次;
    /// 回调可能来自后台线程,UI 侧自行调度。</summary>
    public async Task LoopAsync(Action<string, string> onNewVersion)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        while (true)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
                req.Headers.UserAgent.ParseAdd("PerfPanel");   // GitHub API 要求 UA
                using var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    string tag = doc.RootElement.TryGetProperty("tag_name", out var t)
                        ? t.GetString() ?? "" : "";
                    string url = doc.RootElement.TryGetProperty("html_url", out var u)
                        ? u.GetString() ?? "" : "";
                    if (tag.Length > 0 && tag != LatestTag)
                    {
                        LatestTag = tag;
                        if (url.Length > 0) LatestUrl = url;
                        if (UpdateAvailable) onNewVersion(tag, LatestUrl);
                    }
                }
            }
            catch
            {
                // 网络失败静默,下个周期再试
            }
            await Task.Delay(CheckInterval);
        }
    }
}
