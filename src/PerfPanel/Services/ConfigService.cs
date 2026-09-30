using System.IO;
using System.Text.Json;

namespace PerfPanel.Services;

public sealed class AppConfig
{
    public double Scale { get; set; } = 1.0;          // 界面缩放 0.8~1.3
    public string Orientation { get; set; } = "auto"; // auto=按副屏形状自动选 | portrait | landscape
    public string City { get; set; } = "";            // 手动指定城市;空 = IP 自动定位
    public int WeatherRefreshMinutes { get; set; } = 30;
    public bool ShowWeather { get; set; } = true;
    public bool ShowHint { get; set; } = true;
    public string AmapKey { get; set; } = "";         // 高德 Web服务 Key;空 = Open-Meteo 免费源

    // ---- Coding Plan 额度 ----
    public bool ShowCodingPlan { get; set; } = true;
    public string DeepSeekKey { get; set; } = "";     // DeepSeek 开放平台 API Key
    public string ZhipuKey { get; set; } = "";        // 智谱 open.bigmodel.cn API Key(个人版,Authorization 头直接传)
    public string VolcAk { get; set; } = "";          // 火山引擎 AccessKey ID(控制台 IAM,非推理 API Key)
    public string VolcSk { get; set; } = "";          // 火山引擎 Secret Access Key

    // ---- 便签 / 待办 ----
    public bool ShowNotes { get; set; } = true;       // 便签待办卡片(内容存 notes.json)
}

/// <summary>config.json 配置中心:设置窗口读写,面板即时生效。</summary>
public static class Config
{
    public static AppConfig Current { get; private set; } = new();

    private static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(Path))
                Current = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path)) ?? new AppConfig();
        }
        catch
        {
            Current = new AppConfig();
        }
    }

    public static void Save()
    {
        try
        {
            File.WriteAllText(Path, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
