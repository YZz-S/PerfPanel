using System.Windows;
using PerfPanel.Services;

namespace PerfPanel;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0 && HandleCli(e.Args))
        {
            Shutdown();
            return;
        }

        // 常驻面板:单次 UI 异常不退出
        DispatcherUnhandledException += (_, ex) => ex.Handled = true;
        new MainWindow().Show();
    }

    /// <summary>命令行子命令:--autostart-on/off/status、--set-city 城市名。返回 true 表示处理完毕应退出。</summary>
    private static bool HandleCli(string[] args)
    {
        const string title = "PerfPanel";
        switch (args[0].ToLowerInvariant())
        {
            case "--autostart-on":
            {
                var (ok, msg) = AutostartService.Enable();
                MessageBox.Show(msg, title, MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Error);
                return true;
            }
            case "--autostart-off":
            {
                var r = AutostartService.Disable();
                MessageBox.Show(r.Msg, title, MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
            case "--autostart-status":
                MessageBox.Show(AutostartService.Status(), title, MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return true;
            case "--set-city":
            {
                var city = string.Join(" ", args.Skip(1)).Trim(' ', '"');
                if (city.Length == 0)
                {
                    MessageBox.Show("用法:PerfPanel.exe --set-city 城市名\n例:PerfPanel.exe --set-city 上海",
                        title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return true;
                }
                var res = new WeatherService().SetCityAsync(city).GetAwaiter().GetResult();
                MessageBox.Show(res.Msg, title, MessageBoxButton.OK,
                    res.Ok ? MessageBoxImage.Information : MessageBoxImage.Error);
                return true;
            }
            default:
                return false; // --windowed 等窗口参数交给主窗口
        }
    }
}
