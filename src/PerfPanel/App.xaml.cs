using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PerfPanel.Services;

namespace PerfPanel;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Config.Load();

        if (e.Args.Length > 0 && HandleCli(e.Args))
        {
            Shutdown();
            return;
        }

        // 常驻面板:单次 UI 异常不退出
        DispatcherUnhandledException += (_, ex) => ex.Handled = true;
        new MainWindow().Show();

        // 设置窗自截图(README 效果图/回归冒烟,平行于 MainWindow 的 shot-request.txt 面板截图):
        // exe 旁存在 settings-shot-request.txt(四行 = Tab0默认尺寸 / Tab1默认尺寸 / Tab1收窄470×820 /
        // Tab1收窄点开DeepSeek眼睛,共四张输出 PNG 路径)时,约 2.5s 后打开设置窗按流程渲染后退出;
        // 文件不存在时整个分支无操作。
        string marker = Path.Combine(AppContext.BaseDirectory, "settings-shot-request.txt");
        if (File.Exists(marker))
        {
            var outs = File.ReadAllLines(marker).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            if (outs.Length >= 4) _ = RunSettingsShotAsync(marker, outs[0], outs[1], outs[2], outs[3]);
        }
    }

    private static async System.Threading.Tasks.Task RunSettingsShotAsync(
        string marker, string tab0Path, string tab1Path, string narrowPath, string revealedPath)
    {
        var settings = default(SettingsWindow);
        try
        {
            await System.Threading.Tasks.Task.Delay(2500);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                settings = new SettingsWindow((MainWindow)Application.Current.MainWindow);
                settings.Show();
            });
            if (settings is not { } win) return;
            await System.Threading.Tasks.Task.Delay(1500);
            ShotWindowContent(win, tab0Path);

            // 切到 Coding Plan 额度 Tab(六个密钥行都在这里)
            await Application.Current.Dispatcher.InvokeAsync(() => win.tabMain.SelectedIndex = 1);
            await System.Threading.Tasks.Task.Delay(400);
            ShotWindowContent(win, tab1Path);

            // 窄窗自适应:物理 470×820(SWP_NOMOVE=0x0002 | SWP_NOZORDER=0x0004,保持位置)
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var helper = new WindowInteropHelper(win);
                MonitorHelper.SetWindowPos(helper.Handle, new IntPtr(-1), 0, 0, 470, 820, 0x0006);
            });
            await System.Threading.Tasks.Task.Delay(800);
            ShotWindowContent(win, narrowPath);

            // 密钥显隐:触发 DeepSeek 行眼睛按钮的 Click(与真实点击走同一路径)
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (win.FindName("btnEyeDs") is System.Windows.Controls.Primitives.ButtonBase eye)
                    eye.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            });
            await System.Threading.Tasks.Task.Delay(400);
            ShotWindowContent(win, revealedPath);
        }
        catch { /* 自截图失败不干扰面板 */ }
        finally
        {
            try { if (File.Exists(marker)) File.Delete(marker); } catch { }
            Current.Shutdown(0);
        }
    }

    /// <summary>RenderTargetBitmap 离屏渲染设置窗内容(DIP 尺寸),不依赖屏幕 z 序,免采样被其他置顶窗污染。</summary>
    private static void ShotWindowContent(SettingsWindow? window, string path)
    {
        if (window == null) return;
        var rtb = new RenderTargetBitmap(
            Math.Max(1, (int)window.ActualWidth), Math.Max(1, (int)window.ActualHeight),
            96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(window);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
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
