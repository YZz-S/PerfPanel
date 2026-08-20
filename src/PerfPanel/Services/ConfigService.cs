using System.IO;
using System.Text.Json;

namespace PerfPanel.Services;

public sealed class AppConfig
{
    public double Scale { get; set; } = 1.0;          // 界面缩放 0.8~1.3
    public string City { get; set; } = "";            // 手动指定城市;空 = IP 自动定位
    public int WeatherRefreshMinutes { get; set; } = 30;
    public bool ShowWeather { get; set; } = true;
    public bool ShowHint { get; set; } = true;
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
