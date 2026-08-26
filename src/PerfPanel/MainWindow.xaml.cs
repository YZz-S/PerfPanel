using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PerfPanel.Models;
using PerfPanel.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace PerfPanel;

public partial class MainWindow : Window
{
    private const int HistoryCap = 120;

    private readonly MonitorAggregator _agg = new();
    private readonly WeatherService _weather = new();
    private readonly CodingPlanService _codingPlan = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HistoryBuffer _cpuHist = new(HistoryCap);
    private readonly HistoryBuffer _gpuHist = new(HistoryCap);
    private readonly HistoryBuffer _netHist = new(HistoryCap);
    private readonly bool _windowed;
    private bool _sampling;
    private int _lastPlanMinute = -1;
    private IntPtr _hwnd;
    private SettingsWindow? _settings;

    /// <summary>设置窗口访问面板内部的天气服务。</summary>
    public WeatherService Weather => _weather;

    /// <summary>设置窗口访问面板内部的 Coding Plan 服务。</summary>
    public CodingPlanService CodingPlan => _codingPlan;

    public MainWindow()
    {
        InitializeComponent();
        _windowed = Environment.GetCommandLineArgs().Any(a =>
            a.Equals("--windowed", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-w", StringComparison.OrdinalIgnoreCase));

        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyWindowMode();
        ApplyConfig();

        // 后台初始化 LHM(内核驱动 + 传感器探测,约 1s)
        Task.Run(() =>
        {
            try { _agg.Initialize(); } catch { }
        });

        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        _ = WeatherLoopAsync();
        _ = CodingPlanLoopAsync();
    }

    /// <summary>全屏模式:窗口用 WPF 逻辑像素先摆个大概,句柄创建后再用物理像素精确贴屏。</summary>
    private void ApplyWindowMode()
    {
        if (_windowed)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Title = "PerfPanel(调试窗口)";
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = 480;
            Height = Math.Min(SystemParameters.WorkArea.Height - 40, 1500);
            return;
        }

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 440;   // 临时尺寸,贴屏时被 SetWindowPos 覆盖
        Height = 1920;
        Topmost = true; // 盖住任务栏
        ShowInTaskbar = false;
        txtHint.Text = "拖动顶部移动 · 方向键微调 · Ctrl+方向键调整大小 · F 复位 · S 设置 · Esc 退出";
    }

    /// <summary>应用 config.json 中的显示相关项(设置窗口改动后即时调用)。</summary>
    public void ApplyConfig()
    {
        var c = Config.Current;
        txtHint.Visibility = c.ShowHint ? Visibility.Visible : Visibility.Collapsed;
        if (!c.ShowWeather) cardWeather.Visibility = Visibility.Collapsed;
        if (!c.ShowCodingPlan) cardCodingPlan.Visibility = Visibility.Collapsed;
        ApplyScale(c.Scale);
    }

    /// <summary>缩放:设计画布按 1/s 缩小,Viewbox 等比放大内容铺满屏幕。</summary>
    public void ApplyScale(double s)
    {
        s = Math.Clamp(s, 0.8, 1.3);
        layoutRoot.Width = 440 / s;
        layoutRoot.Height = 1920 / s;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!_windowed)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SnapToMonitor();
        }
    }

    /// <summary>物理像素精确铺满目标副屏(避开 WPF 逻辑像素在混合 DPI 下的偏移)。</summary>
    private void SnapToMonitor()
    {
        var target = FindTargetMonitor();
        if (target == null) return;
        const uint swpShowWindow = 0x0040;
        MonitorHelper.SetWindowPos(_hwnd, new IntPtr(-1) /*HWND_TOPMOST*/,
            target.Left, target.Top, target.Width, target.Height, swpShowWindow);
    }

    private static MonitorHelper.MonitorBounds? FindTargetMonitor()
    {
        var monitors = MonitorHelper.GetAll();
        return monitors.FirstOrDefault(m => m.Width == 440 && m.Height == 1920)
            ?? monitors.FirstOrDefault(m => m.Width == 1920 && m.Height == 440)
            ?? monitors.FirstOrDefault(m => !m.Primary)
            ?? monitors.FirstOrDefault();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            return;
        }

        if (e.Key == Key.S)
        {
            OpenSettings();
            return;
        }

        if (_windowed || _hwnd == IntPtr.Zero) return;

        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.F:
                SnapToMonitor();
                break;
            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
                MoveOrResize(e.Key, step, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                break;
        }
    }

    private void MoveOrResize(Key key, int step, bool resize)
    {
        MonitorHelper.GetWindowRect(_hwnd, out var r);
        int x = r.Left, y = r.Top, w = r.Right - r.Left, h = r.Bottom - r.Top;
        switch (key)
        {
            case Key.Left: if (resize) w -= step; else x -= step; break;
            case Key.Right: if (resize) w += step; else x += step; break;
            case Key.Up: if (resize) h -= step; else y -= step; break;
            case Key.Down: if (resize) h += step; else y += step; break;
        }
        MonitorHelper.SetWindowPos(_hwnd, new IntPtr(-1), x, y, Math.Max(w, 100), Math.Max(h, 100), 0);
    }

    /// <summary>无边框窗口:按住头部区域拖动整窗。</summary>
    private void Header_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_windowed && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Gear_OnClick(object sender, MouseButtonEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        if (_settings is { IsLoaded: true })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsWindow(this) { Owner = this };
        _settings.Show();
    }

    private async Task TickAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            SensorSnapshot? snap = null;
            await Task.Run(() => snap = _agg.Sample());
            if (snap != null && IsLoaded)
                Dispatcher.Invoke(() => UpdateUI(snap));
        }
        catch { /* 单次采样失败不终止面板 */ }
        finally { _sampling = false; }
    }

    private void UpdateUI(SensorSnapshot s)
    {
        // ---- 头部 ----
        txtTime.Text = s.Time.ToString("HH:mm:ss");
        txtDate.Text = s.Time.ToString("yyyy年M月d日 ") + Weekday(s.Time);
        txtUptime.Text = "开机 " + FormatUptime(s.Uptime);

        bool full = s.FullSensorMode;
        dotMode.Fill = full ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))
                            : new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        txtMode.Text = full ? "FULL" : "BASIC";
        txtFooter.Text = !full
            ? "SYSTEM MONITOR · BASIC(管理员可解锁温度)"
            : s.CpuTemp is null
                ? "SYSTEM MONITOR · FULL · CPU温度/功耗需以管理员身份运行"
                : "SYSTEM MONITOR · FULL SENSORS";

        // ---- CPU ----
        txtCpuName.Text = TrimModel(s.CpuName);
        if (s.CpuLoad is { } cl)
        {
            txtCpuLoad.Text = $"{cl:F0}%";
            txtCpuLoad.Foreground = LoadBrush(cl);
            SetBar(barCpuFill, (barCpuFill.Parent as Border)?.ActualWidth ?? 0, cl / 100f);
            _cpuHist.Push(cl);
            cpuGraph.Values = _cpuHist.ToArray();
        }

        UpdateRow(rowCpuFreq, txtCpuFreq, s.CpuFreq, v => $"{v:F2} GHz");
        UpdateRow(rowCpuTemp, txtCpuTemp, s.CpuTemp, v => $"{v:F0} °C", TempBrush);
        UpdateRow(rowCpuPower, txtCpuPower, s.CpuPower, v => $"{v:F1} W");
        UpdateRow(rowCpuFan, txtCpuFan, s.CpuFan, v => $"{v:F0} RPM");

        // ---- GPU ----
        txtGpuName.Text = TrimModel(s.GpuName);
        if (s.GpuLoad is { } gl)
        {
            txtGpuLoad.Text = $"{gl:F0}%";
            txtGpuLoad.Foreground = LoadBrush(gl);
            SetBar(barGpuFill, (barGpuFill.Parent as Border)?.ActualWidth ?? 0, gl / 100f);
            _gpuHist.Push(gl);
            gpuGraph.Values = _gpuHist.ToArray();
        }

        if (s.GpuVramUsedGb is { } used)
        {
            rowGpuVram.Visibility = Visibility.Visible;
            txtGpuVram.Text = s.GpuVramTotalGb is { } total ? $"{used:F1} / {total:F1} GB" : $"{used:F1} GB";
            if (s.GpuVramTotalGb is { } tt)
                SetBar(barGpuVramFill, (barGpuVramFill.Parent as Border)?.ActualWidth ?? 0, used / tt);
        }
        else rowGpuVram.Visibility = Visibility.Collapsed;

        UpdateRow(rowGpuTemp, txtGpuTemp, s.GpuTemp, v => $"{v:F0} °C", TempBrush);
        UpdateRow(rowGpuPower, txtGpuPower, s.GpuPower, v => $"{v:F1} W");
        UpdateRow(rowGpuFan, txtGpuFan, s.GpuFan,
            s.GpuFanPercent ? v => $"{v:F0}%" : v => $"{v:F0} RPM");

        // ---- 内存 ----
        txtRamPct.Text = $"{s.RamLoad:F0}%";
        txtRamPct.Foreground = LoadBrush(s.RamLoad);
        txtRamDetail.Text = $"{s.RamUsedGb:F1} / {s.RamTotalGb:F1} GB";
        SetBar(barRamFill, (barRamFill.Parent as Border)?.ActualWidth ?? 0, s.RamLoad / 100f);

        // ---- 网络 ----
        txtNetIf.Text = s.NetInterface;
        txtNetDown.Text = FormatSpeed(s.NetDownBps);
        txtNetUp.Text = FormatSpeed(s.NetUpBps);
        _netHist.Push(s.NetDownBps / 1024f);
        netGraph.Values = _netHist.ToArray();

        // ---- 天气 ----
        UpdateWeather();

        // ---- Coding Plan(分钟级节流,重置倒计时按分钟走) ----
        if (s.Time.Minute != _lastPlanMinute)
        {
            _lastPlanMinute = s.Time.Minute;
            UpdateCodingPlan();
        }
    }

    private void UpdateWeather()
    {
        if (!Config.Current.ShowWeather)
        {
            cardWeather.Visibility = Visibility.Collapsed;
            return;
        }
        var w = _weather.Current;
        if (w == null) return;
        cardWeather.Visibility = Visibility.Visible;
        txtWeatherTemp.Text = $"{w.TempC:F0}°";
        txtWeatherDesc.Text = w.HumidityPct is { } h ? $"{w.Desc} · 湿度{h}%" : w.Desc;
        txtWeatherRange.Text = $"H {w.TMax:F0}°  L {w.TMin:F0}°";

        string loc = string.IsNullOrEmpty(w.City) ? "本地" : w.City;
        int remain = (int)Math.Ceiling((_weather.NextRefreshUtc - DateTime.UtcNow).TotalMinutes);
        txtWeatherCity.Text = remain > 0 ? $"{loc} · {remain}分钟后刷新" : $"{loc} · 刷新中…";

        txtWind.Text = $"{w.WindLevel}级 {w.WindSpeedKmh:F0}km/h";
        txtUv.Text = $"{w.UvIndex:F0} {w.UvLevel}";
        if (w.Aqi is { } aqi)
        {
            cellAqi.Visibility = Visibility.Visible;
            txtAqi.Text = $"{aqi} {w.AqiLevel}";
            txtAqi.Foreground = aqi switch
            {
                <= 50 => new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)),
                <= 100 => new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
                _ => new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
            };
        }
        else
        {
            cellAqi.Visibility = Visibility.Collapsed;
        }
    }

    private async Task WeatherLoopAsync()
    {
        while (true)
        {
            var w = await _weather.FetchAsync();
            if (w != null && IsLoaded)
                await Dispatcher.InvokeAsync(UpdateWeather);
            await Task.Delay(WeatherService.RefreshInterval);
        }
    }

    // ---------- Coding Plan 额度 ----------

    private async Task CodingPlanLoopAsync()
    {
        // 启动稍等,避免与天气定位/LHM 初始化抢带宽
        await Task.Delay(TimeSpan.FromSeconds(5));
        while (true)
        {
            await RefreshCodingPlanAsync();
            await Task.Delay(CodingPlanService.RefreshInterval);
        }
    }

    /// <summary>立即刷新全部 Coding Plan 额度并更新卡片(设置窗口保存密钥后也调用)。</summary>
    public async Task RefreshCodingPlanAsync()
    {
        if (_codingPlan.AnyKeyConfigured)
            await _codingPlan.FetchAllAsync();
        if (IsLoaded)
            await Dispatcher.InvokeAsync(UpdateCodingPlan);
    }

    private void UpdateCodingPlan()
    {
        var c = Config.Current;
        if (!c.ShowCodingPlan || !_codingPlan.AnyKeyConfigured)
        {
            cardCodingPlan.Visibility = Visibility.Collapsed;
            return;
        }
        cardCodingPlan.Visibility = Visibility.Visible;

        if (_codingPlan.Current.Count == 0)
        {
            txtPlanRefresh.Text = "查询中…";
            return;
        }
        int remain = (int)Math.Ceiling((_codingPlan.LastRefreshUtc + CodingPlanService.RefreshInterval - DateTime.UtcNow).TotalMinutes);
        txtPlanRefresh.Text = remain > 0 ? $"{remain}分钟后刷新" : "刷新中…";

        planRows.Children.Clear();
        foreach (var p in _codingPlan.Current)
            planRows.Children.Add(BuildPlanSection(p));
    }

    /// <summary>构建一个供应商区块:标题行(名称+主值) + 每限额窗口一行(标签/用量条/剩余/重置倒计时)。</summary>
    private static StackPanel BuildPlanSection(PlanStatus p)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

        // ---- 标题行 ----
        var head = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        var title = new StackPanel();
        var name = new TextBlock
        {
            Text = p.PlanName is { Length: > 0 } plan ? $"{p.Name} · {plan}" : p.Name,
            FontSize = 13, FontWeight = FontWeights.Bold, Foreground = BrushFromHex("#C9D6EE"),
        };
        title.Children.Add(name);
        if (p.StaleError is { Length: > 0 })
        {
            title.Children.Add(new TextBlock
            {
                Text = "旧数据(上次刷新失败)", FontSize = 10, Foreground = BrushFromHex("#55648A"),
            });
        }
        head.Children.Add(title);

        if (!p.Ok)
        {
            head.Children.Add(new TextBlock
            {
                Text = "✗", FontSize = 15, FontWeight = FontWeights.Bold,
                Foreground = BrushFromHex("#F87171"), HorizontalAlignment = HorizontalAlignment.Right,
            });
            panel.Children.Add(head);
            panel.Children.Add(new TextBlock
            {
                Text = p.Error ?? "查询失败", FontSize = 11, Foreground = BrushFromHex("#F87171"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0),
            });
            return panel;
        }

        // 主值取第一个窗口
        var main = p.Windows.FirstOrDefault();
        head.Children.Add(new TextBlock
        {
            Text = FormatPlanValue(main, p.Unit),
            FontSize = 22, FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Consolas"),
            Foreground = PlanValueBrush(main), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(head);

        // ---- 窗口行 ----
        foreach (var w in p.Windows)
        {
            // 绝对值型(DeepSeek 余额)主值已展示,不再重复成行
            if (w.UsedPercent is not { } used) continue;
            var row = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = w.Label, FontSize = 11, Foreground = BrushFromHex("#55648A"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
            });

            // 用量条(百分比型)
            {
                var track = new Border
                {
                    Height = 4, CornerRadius = new CornerRadius(2), ClipToBounds = true,
                    Background = BrushFromHex("#1B2743"), VerticalAlignment = VerticalAlignment.Center,
                };
                var fill = new Border
                {
                    CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left,
                    Background = PlanValueBrush(w), Width = 0,
                };
                track.SizeChanged += (_, e) => fill.Width = Math.Clamp(used / 100.0, 0, 1) * e.NewSize.Width;
                track.Child = fill;
                Grid.SetColumn(track, 1);
                row.Children.Add(track);
            }

            var value = new TextBlock
            {
                Text = FormatPlanValue(w, p.Unit, withReset: true),
                FontSize = 12, FontFamily = new FontFamily("Consolas"), Foreground = BrushFromHex("#8FA3C8"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            };
            Grid.SetColumn(value, 2);
            row.Children.Add(value);

            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>窗口值文本:百分比型"剩87% · 2.5小时后重置";绝对值型"110.2 点"/"¥110.20"。</summary>
    private static string FormatPlanValue(QuotaWindow? w, string? unit, bool withReset = false)
    {
        if (w == null) return "--";
        if (w.RemainingValue is { } abs)
            return unit == "CNY" ? $"¥{abs:0.00}"
                 : unit is { Length: > 0 } u ? $"{abs:0.##} {u}"
                 : $"{abs:0.##}";
        double remain = 100 - (w.UsedPercent ?? 0);
        string reset = withReset ? FormatResetIn(w.ResetUtc) : "";
        return reset.Length > 0 ? $"剩{remain:F0}% {reset}" : $"剩{remain:F0}%";
    }

    /// <summary>重置倒计时(相对时间,分钟起)。</summary>
    private static string FormatResetIn(DateTime? resetUtc)
    {
        if (resetUtc == null) return "";
        var span = resetUtc.Value - DateTime.UtcNow;
        if (span <= TimeSpan.Zero) return "· 即将重置";
        if (span.TotalMinutes < 60) return $"· {(int)Math.Ceiling(span.TotalMinutes)}分钟后重置";
        if (span.TotalHours < 48) return $"· {span.TotalHours:F1}小时后重置";
        return $"· {span.TotalDays:F1}天后重置";
    }

    /// <summary>按剩余量着色:充足青、偏低黄、告急红(绝对值型无百分比,用青)。</summary>
    private static Brush PlanValueBrush(QuotaWindow? w)
    {
        double remain = w?.RemainingValue != null && w?.UsedPercent == null ? 100 : 100 - (w?.UsedPercent ?? 0);
        return remain switch
        {
            < 10 => BrushFromHex("#F87171"),
            < 30 => BrushFromHex("#FBBF24"),
            _ => BrushFromHex("#22D3EE"),
        };
    }

    private static Brush BrushFromHex(string hex) =>
        new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    // ---------- 辅助 ----------

    private static void UpdateRow<T>(FrameworkElement row, System.Windows.Controls.TextBlock txt,
        T? value, Func<T, string> fmt, Func<T, Brush>? color = null) where T : struct
    {
        if (value is { } v)
        {
            row.Visibility = Visibility.Visible;
            txt.Text = fmt(v);
            if (color != null) txt.Foreground = color(v);
        }
        else
        {
            row.Visibility = Visibility.Collapsed;
        }
    }

    private static void SetBar(FrameworkElement fill, double trackWidth, float ratio)
    {
        if (trackWidth <= 0 || double.IsNaN(trackWidth)) return;
        fill.Width = Math.Clamp(ratio, 0f, 1f) * trackWidth;
    }

    private static Brush LoadBrush(float pct) => pct switch
    {
        > 92 => new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
        > 75 => new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
        _ => new SolidColorBrush(Color.FromRgb(0x22, 0xD3, 0xEE)),
    };

    private static Brush TempBrush(float t) => t switch
    {
        >= 85 => new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)),
        >= 70 => new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
        >= 50 => new SolidColorBrush(Color.FromRgb(0x67, 0xE8, 0xF9)),
        _ => new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)),
    };

    private static string Weekday(DateTime d) => d.DayOfWeek switch
    {
        DayOfWeek.Monday => "星期一",
        DayOfWeek.Tuesday => "星期二",
        DayOfWeek.Wednesday => "星期三",
        DayOfWeek.Thursday => "星期四",
        DayOfWeek.Friday => "星期五",
        DayOfWeek.Saturday => "星期六",
        _ => "星期日",
    };

    private static string FormatUptime(TimeSpan up)
    {
        if (up.TotalMinutes < 1) return "刚开机";
        if (up.Days > 0) return $"{up.Days}天 {up.Hours:00}:{up.Minutes:00}";
        return $"{up.Hours:00}:{up.Minutes:00}:{up.Seconds:00}";
    }

    private static string FormatSpeed(long bytesPerSec) => bytesPerSec switch
    {
        < 1024 => $"{bytesPerSec} B/s",
        < 1024 * 1024 => $"{bytesPerSec / 1024f:F0} KB/s",
        _ => $"{bytesPerSec / (1024f * 1024f):F1} MB/s",
    };

    /// <summary>CPU/GPU 商标名去冗余:"Intel(R) Core(TM) i7-..." → "i7-..." 常见风格。</summary>
    private static string TrimModel(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var s = name.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", "")
                    .Replace("Intel Core", "Intel").Replace("AMD ", "");
        return s.Length > 34 ? s[..34] : s;
    }

    private sealed class HistoryBuffer(int capacity)
    {
        private readonly float[] _buf = new float[capacity];
        private int _count;

        public void Push(float v)
        {
            if (_count < capacity)
            {
                _buf[_count++] = v;
            }
            else
            {
                Array.Copy(_buf, 1, _buf, 0, capacity - 1);
                _buf[capacity - 1] = v;
            }
        }

        public float[] ToArray() => _buf[.._count];
    }
}
