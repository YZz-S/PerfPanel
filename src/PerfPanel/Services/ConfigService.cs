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
    public bool ShowFooter { get; set; } = true;      // 底部 SYSTEM MONITOR 状态行
    public string BrandText { get; set; } = "PERF PANEL"; // 左上角标题文字;空 = 隐藏
    public int GpuIndex { get; set; } = -1;           // 监控的 GPU 序号(LHM 枚举顺序);-1 = 自动(优先有温度的独显)

    // ---- 背景自定义 ----
    public string BackgroundImagePath { get; set; } = ""; // 自定义背景图(设置里选择后复制到 exe 旁的副本路径;空 = 默认深色底)
    public double BackgroundOpacity { get; set; } = 0.45; // 背景图不透明度 0~1
    public double CardOpacity { get; set; } = 1.0;        // 卡片不透明度 0~1(1 = 不透明;调低可透出背景图)
    public string AmapKey { get; set; } = "";         // 高德 Web服务 Key;空 = Open-Meteo 免费源

    // ---- Coding Plan 额度 ----
    public bool ShowCodingPlan { get; set; } = true;
    public List<string> PlanOrder { get; set; } = new();  // 供应商显示顺序;未列出的按默认顺序排在后面
    public List<string> PlanHidden { get; set; } = new(); // 隐藏的供应商名称(卡片不再查询展示该项)
    public string DeepSeekKey { get; set; } = "";     // DeepSeek 开放平台 API Key
    public string ZhipuKey { get; set; } = "";        // 智谱 open.bigmodel.cn API Key(个人版,Authorization 头直接传)
    public string VolcAk { get; set; } = "";          // 火山引擎 AccessKey ID(控制台 IAM,非推理 API Key)
    public string VolcSk { get; set; } = "";          // 火山引擎 Secret Access Key
    public string MimoCookie { get; set; } = "";      // MiMo 控制台 Cookie 头(须含 api-platform_serviceToken + userId;查余额与 Token Plan 用量)
    public string OpenCodeKey { get; set; } = "";     // OpenCode Go API Key(sk-,opencode.ai 控制台;查 Go 订阅 5h/周/月用量)

    // ---- LM Studio 本地推理 ----
    public bool ShowLmStudio { get; set; } = true;    // LM Studio 监控条(加载模型/并发调用/prefill·decode 速度)
    public int LmStudioPort { get; set; } = 1234;     // 本地服务端口(LM Studio 开发者页 → Server)
    public string LmStudioLogDir { get; set; } = "";  // 服务日志目录;空 = %USERPROFILE%\.lmstudio\server-logs

    // ---- 便签 / 待办 ----
    public bool ShowNotes { get; set; } = true;       // 便签待办卡片(内容存 notes.json)
    public bool ShowPomodoro { get; set; } = true;    // 便签卡内的番茄钟计时器
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
