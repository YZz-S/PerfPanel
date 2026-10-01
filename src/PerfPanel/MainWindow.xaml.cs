using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PerfPanel.Controls;
using PerfPanel.Models;
using PerfPanel.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace PerfPanel;

/// <summary>面板方向:竖版 440×1920 / 横版 1920×440,两套骨架共用同一批卡片。</summary>
public enum PanelOrientation { Portrait, Landscape }

public partial class MainWindow : Window
{
    private const int HistoryCap = 120;

    private readonly MonitorAggregator _agg = new();
    private readonly WeatherService _weather = new();
    private readonly CodingPlanService _codingPlan = new();
    private readonly NotesService _notes = NotesService.Instance;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly HistoryBuffer _cpuHist = new(HistoryCap);
    private readonly HistoryBuffer _gpuHist = new(HistoryCap);
    private readonly HistoryBuffer _netHist = new(HistoryCap);
    private readonly bool _windowed;
    private readonly PanelOrientation? _cliOrientation;
    private PanelOrientation _orientation = PanelOrientation.Portrait;
    private double _todoFontSize = 13;   // 待办行字号(横屏大卡片放大)
    private bool _sampling;
    private int _lastPlanMinute = -1;
    private DateTime _reminderActiveUntilUtc = DateTime.MinValue;
    private IntPtr _hwnd;
    private SettingsWindow? _settings;

    /// <summary>设置窗口访问面板内部的天气服务。</summary>
    public WeatherService Weather => _weather;

    /// <summary>设置窗口访问面板内部的 Coding Plan 服务。</summary>
    public CodingPlanService CodingPlan => _codingPlan;

    /// <summary>设置窗口访问便签/待办服务。</summary>
    public NotesService Notes => _notes;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            Icon = BitmapFrame.Create(new Uri("pack://application:,,,/app.ico")); // 窗口化调试时的标题栏/任务栏图标
        }
        catch { }
        _notes.Load();
        var args = Environment.GetCommandLineArgs();
        _windowed = args.Any(a =>
            a.Equals("--windowed", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-w", StringComparison.OrdinalIgnoreCase));
        _cliOrientation =
            args.Any(a => a.Equals("--landscape", StringComparison.OrdinalIgnoreCase)) ? PanelOrientation.Landscape :
            args.Any(a => a.Equals("--portrait", StringComparison.OrdinalIgnoreCase)) ? PanelOrientation.Portrait : null;

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
        RebuildTodoRows();
        _ = WeatherLoopAsync();
        _ = CodingPlanLoopAsync();

        // 全屏模式:启动后 2s/6s 两次重新贴屏(纠正 DPI 切换导致的窗口缩水),并周期性重申置顶,
        // 保证开机自启动后面板始终铺满副屏、不被任务栏盖住底部
        if (!_windowed)
        {
            var bootstraps = new[] { 2000, 6000 };
            foreach (var delay in bootstraps)
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    SnapToMonitor();
                };
                t.Start();
            }
            var topmost = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            topmost.Tick += (_, _) => ReassertTopmost();
            topmost.Start();
        }
    }

    /// <summary>重申 TOPMOST(不动位置尺寸、不抢焦点),压住任务栏保证面板完整可见。</summary>
    private void ReassertTopmost()
    {
        if (_hwnd == IntPtr.Zero) return;
        const uint swpNomoveNosizeNoActivate = 0x0001 | 0x0002 | 0x0010;
        MonitorHelper.SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, swpNomoveNosizeNoActivate);
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
            if (_orientation == PanelOrientation.Landscape)
            {
                Width = Math.Min(SystemParameters.WorkArea.Width - 40, 1700);
                Height = Math.Min(SystemParameters.WorkArea.Height - 40, Width * 440.0 / 1920.0);
            }
            else
            {
                Width = 480;
                Height = Math.Min(SystemParameters.WorkArea.Height - 40, 1500);
            }
            return;
        }

        bool land = _orientation == PanelOrientation.Landscape;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = land ? 1920 : 440;   // 临时尺寸,贴屏时被 SetWindowPos 覆盖
        Height = land ? 440 : 1920;
        Topmost = true; // 盖住任务栏
        ShowInTaskbar = false;
        txtHint.Text = "拖动顶部移动 · 方向键微调 · Ctrl+方向键调整大小 · F 复位 · S 设置 · Esc 退出";
    }

    /// <summary>应用 config.json 中的显示相关项(设置窗口改动后即时调用)。</summary>
    public void ApplyConfig()
    {
        var c = Config.Current;
        txtHint.Visibility = c.ShowHint ? Visibility.Visible : Visibility.Collapsed;
        txtFooter.Visibility = c.ShowFooter ? Visibility.Visible : Visibility.Collapsed;
        if (!c.ShowWeather) cardWeather.Visibility = Visibility.Collapsed;
        if (!c.ShowCodingPlan) cardCodingPlan.Visibility = Visibility.Collapsed;
        if (!c.ShowNotes) cardNotes.Visibility = Visibility.Collapsed;
        ApplyScale(c.Scale);
        UpdateLandscapeSpans();
    }

    /// <summary>缩放:设计画布按 1/s 缩小,Viewbox 等比放大内容铺满屏幕(竖 440×1920 / 横 1920×440)。</summary>
    public void ApplyScale(double s)
    {
        s = Math.Clamp(s, 0.8, 1.3);
        bool land = _orientation == PanelOrientation.Landscape;
        layoutRoot.Width = (land ? 1920.0 : 440.0) / s;
        layoutRoot.Height = (land ? 440.0 : 1920.0) / s;
    }

    // ---------- 横竖屏布局 ----------

    /// <summary>解析方向并重排布局(方向选择:命令行 > config > 屏幕形状;设置窗口改动后也调用)。</summary>
    public void ApplyOrientation()
    {
        _orientation = ResolveOrientation();
        if (_orientation == PanelOrientation.Landscape) ArrangeLandscape();
        else ArrangePortrait();
        ApplyScale(Config.Current.Scale);
        if (!_windowed && _hwnd != IntPtr.Zero) SnapToMonitor();
    }

    private PanelOrientation ResolveOrientation()
    {
        if (_cliOrientation is { } cli) return cli;
        switch (Config.Current.Orientation)
        {
            case "portrait": return PanelOrientation.Portrait;
            case "landscape": return PanelOrientation.Landscape;
            default: // auto:全屏按目标副屏形状,窗口化保持竖版
                if (_windowed) return PanelOrientation.Portrait;
                var m = FindTargetMonitor(PanelOrientation.Portrait);
                return m is { } && m.Width > m.Height ? PanelOrientation.Landscape : PanelOrientation.Portrait;
        }
    }

    /// <summary>把元素从原容器摘出放进目标 Panel;已在目标容器则不动(幂等,可反复调用)。</summary>
    private static void MoveTo(Panel parent, FrameworkElement el, int? index = null)
    {
        switch (el.Parent)
        {
            case ContentControl cc: cc.Content = null; break;
            case Panel old when !ReferenceEquals(old, parent): old.Children.Remove(el); break;
        }
        if (ReferenceEquals(el.Parent, parent)) return;
        if (index is { } i) parent.Children.Insert(i, el);
        else parent.Children.Add(el);
    }

    /// <summary>把卡片挂进横版 ContentControl 单元格;已在则不动。传 null 清空。</summary>
    private static void MoveTo(ContentControl cell, FrameworkElement? el)
    {
        if (el == null) { cell.Content = null; return; }
        switch (el.Parent)
        {
            case ContentControl cc when !ReferenceEquals(cc, cell): cc.Content = null; break;
            case Panel old: old.Children.Remove(el); break;
        }
        cell.Content = el;
    }

    private void ArrangePortrait()
    {
        landscapeRoot.Visibility = Visibility.Collapsed;
        portraitRoot.Visibility = Visibility.Visible;

        MoveTo(portRightCluster, btnGear);
        MoveTo(portRightCluster, dotMode);
        MoveTo(portRightCluster, txtMode);
        MoveTo(portHeader, txtTime, 1);
        MoveTo(portHeader, txtHint, 3);
        MoveTo(portDateRow, txtDate);
        MoveTo(portDateRow, txtUptime);
        MoveTo(portFooter, txtFooter);

        txtTime.FontSize = 58;
        txtTime.Margin = new Thickness(0, 10, 0, 0);
        txtHint.Margin = new Thickness(0, 8, 0, 0);
        txtDate.Margin = new Thickness(0);
        txtDate.VerticalAlignment = VerticalAlignment.Stretch;
        txtUptime.Margin = new Thickness(0);
        txtUptime.VerticalAlignment = VerticalAlignment.Stretch;
        btnGear.Margin = new Thickness(0, 0, 12, 0);

        foreach (var card in new FrameworkElement[]
                 {
                     cardCpu, cardGpu, cardMemory, cardNetwork, cardNotes, cardWeather, cardCodingPlan
                 })
        {
            card.Margin = new Thickness(0, 0, 0, 12);
            MoveTo(portCards, card);
        }

        // 竖版:大数字 + 进度条 + 历史曲线,统计 2×2 全量
        SetCompactTiles(compact: false);
        cpuGraph.Visibility = Visibility.Visible;
        gpuGraph.Visibility = Visibility.Visible;
        cpuGraph.Height = 96;
        gpuGraph.Height = 96;
        netGraph.Height = 64;
        txtCpuName.MaxWidth = 240;
        txtGpuName.MaxWidth = 240;
        txtNoteText.FontSize = 15;
        txtNotesReminder.FontSize = 12;
        _todoFontSize = 13;
    }

    private void ArrangeLandscape()
    {
        portraitRoot.Visibility = Visibility.Collapsed;
        landscapeRoot.Visibility = Visibility.Visible;

        MoveTo(landClockCell, txtTime);
        MoveTo(landClockCell, txtHint);
        MoveTo(landInfoCell, txtDate);
        MoveTo(landInfoCell, txtUptime);
        MoveTo(landInfoCell, btnGear);
        MoveTo(landInfoCell, dotMode);
        MoveTo(landInfoCell, txtMode);
        MoveTo(landFooter, txtFooter);

        txtTime.FontSize = 44;
        txtTime.Margin = new Thickness(0);
        txtHint.Margin = new Thickness(0, 2, 0, 0);
        txtDate.Margin = new Thickness(0);
        txtDate.VerticalAlignment = VerticalAlignment.Center;
        txtUptime.Margin = new Thickness(18, 0, 0, 0);
        txtUptime.VerticalAlignment = VerticalAlignment.Center;
        btnGear.Margin = new Thickness(24, 0, 0, 0);

        // 紧凑磁贴:圆环表示用量,无历史曲线,统计行压缩
        SetCompactTiles(compact: true);
        cpuGraph.Visibility = Visibility.Collapsed;
        gpuGraph.Visibility = Visibility.Collapsed;
        netGraph.Height = double.NaN;
        txtCpuName.MaxWidth = 330;
        txtGpuName.MaxWidth = 330;
        txtNoteText.FontSize = 18;
        txtNotesReminder.FontSize = 13;
        _todoFontSize = 15;

        MoveTo(landCpuCell, cardCpu);
        cardCpu.Margin = new Thickness(0, 0, 12, 12);
        MoveTo(landGpuCell, cardGpu);
        cardGpu.Margin = new Thickness(0, 0, 12, 12);
        MoveTo(landMemCell, cardMemory);
        cardMemory.Margin = new Thickness(0, 0, 12, 12);
        MoveTo(landNetCell, cardNetwork);
        cardNetwork.Margin = new Thickness(0, 0, 12, 0);
        MoveTo(landNotesCell, cardNotes);
        cardNotes.Margin = new Thickness(0, 0, 12, 0);

        // 天气/额度占右列上下两格,空缺时另一半纵跨两行
        UpdateLandscapeSpans();
    }

    /// <summary>横屏紧凑磁贴(CPU/GPU/内存):圆环显示用量,隐藏大数字/进度条;
    /// 统计 2×2 重排为单行四列并缩小字号,保证磁贴压进上排高度。竖屏还原。</summary>
    private void SetCompactTiles(bool compact)
    {
        var vis = compact ? Visibility.Visible : Visibility.Collapsed;
        ringCpu.Visibility = vis;
        ringGpu.Visibility = vis;
        ringRam.Visibility = vis;
        txtCpuLoad.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        txtGpuLoad.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        txtRamPct.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        barCpuTrack.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        barGpuTrack.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        barRamTrack.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;

        double statFont = compact ? 12 : 19;
        foreach (var txt in new[] { txtCpuFreq, txtCpuTemp, txtCpuPower, txtCpuFan, txtGpuVram, txtGpuTemp, txtGpuPower, txtGpuFan })
            txt.FontSize = statFont;

        foreach (var (grid, cells) in new[]
                 {
                     (gridCpuStats, new[] { rowCpuFreq, rowCpuTemp, rowCpuPower, rowCpuFan }),
                     (gridGpuStats, new[] { rowGpuVram, rowGpuTemp, rowGpuPower, rowGpuFan }),
                 })
        {
            if (compact)
            {
                // 单行四列:磁贴高度只剩 头部(圆环) + 一行统计
                grid.RowDefinitions.Clear();
                grid.ColumnDefinitions.Clear();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (int i = 0; i < 4; i++)
                {
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    Grid.SetRow(cells[i], 0);
                    Grid.SetColumn(cells[i], i);
                }
            }
            else
            {
                grid.RowDefinitions.Clear();
                grid.ColumnDefinitions.Clear();
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 46 });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, MinHeight = 46 });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int i = 0; i < 4; i++)
                {
                    Grid.SetRow(cells[i], i / 2);
                    Grid.SetColumn(cells[i], i % 2);
                }
            }
        }
    }

    /// <summary>横版右列:天气/额度按可见性占格,某卡片隐藏时另一半纵跨两行;便签卡挂左下大格。竖版无格位概念,直接跳过。</summary>
    private void UpdateLandscapeSpans()
    {
        if (_orientation != PanelOrientation.Landscape) return;
        bool wx = cardWeather.Visibility == Visibility.Visible;
        bool plan = cardCodingPlan.Visibility == Visibility.Visible;
        Grid.SetRowSpan(landWxCell, 1);
        Grid.SetRowSpan(landPlanCell, 1);
        if (wx && plan)
        {
            MoveTo(landWxCell, cardWeather);
            cardWeather.Margin = new Thickness(0, 0, 12, 12);
            MoveTo(landPlanCell, cardCodingPlan);
            cardCodingPlan.Margin = new Thickness(0, 0, 12, 0);
        }
        else if (wx)
        {
            MoveTo(landWxCell, cardWeather);
            cardWeather.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetRowSpan(landWxCell, 2);
        }
        else if (plan)
        {
            MoveTo(landPlanCell, cardCodingPlan);
            cardCodingPlan.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetRowSpan(landPlanCell, 2);
        }

        if (Config.Current.ShowNotes)
            MoveTo(landNotesCell, cardNotes);
        else
            MoveTo(landNotesCell, null);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyOrientation();
        if (!_windowed)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SnapToMonitor();
        }
    }

    /// <summary>物理像素精确铺满目标副屏(避开 WPF 逻辑像素在混合 DPI 下的偏移)。</summary>
    private void SnapToMonitor()
    {
        var target = FindTargetMonitor(_orientation);
        if (target == null) return;
        const uint swpShowWindow = 0x0040;
        MonitorHelper.SetWindowPos(_hwnd, new IntPtr(-1) /*HWND_TOPMOST*/,
            target.Left, target.Top, target.Width, target.Height, swpShowWindow);
    }

    /// <summary>优先选与当前方向同形状的副屏(440×1920 / 1920×440),其次任意副屏。</summary>
    private static MonitorHelper.MonitorBounds? FindTargetMonitor(PanelOrientation orientation)
    {
        var monitors = MonitorHelper.GetAll();
        MonitorHelper.MonitorBounds? Exact(bool landscape) => monitors.FirstOrDefault(m =>
            landscape ? m.Width == 1920 && m.Height == 440 : m.Width == 440 && m.Height == 1920);
        return Exact(orientation == PanelOrientation.Landscape)
            ?? Exact(orientation == PanelOrientation.Portrait)
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
        // 真实开机时刻(系统最后启动的墙钟时间) + 运行时长,避免"开机 HH:mm:ss"被误读成时刻
        var boot = s.Time - s.Uptime;
        txtUptime.Text = $"开机 {boot:MM-dd HH:mm} · 已运行 {FormatUptime(s.Uptime)}";

        bool full = s.FullSensorMode;
        dotMode.Fill = full ? new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99))
                            : new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        txtMode.Text = full ? "FULL" : "BASIC";
        txtFooter.Text = !full
            ? "SYSTEM MONITOR · BASIC(管理员可解锁温度)"
            : s.CpuTemp is null
                ? "SYSTEM MONITOR · FULL · CPU温度不可用(需安装 PawnIO 驱动)"
                : "SYSTEM MONITOR · FULL SENSORS";

        // ---- CPU ----
        txtCpuName.Text = TrimModel(s.CpuName);
        if (s.CpuLoad is { } cl)
        {
            txtCpuLoad.Text = $"{cl:F0}%";
            txtCpuLoad.Foreground = LoadBrush(cl);
            SetBar(barCpuFill, (barCpuFill.Parent as Border)?.ActualWidth ?? 0, cl / 100f);
            ringCpu.Value = cl;
            ringCpu.Text = $"{cl:F0}%";
            ringCpu.RingBrush = LoadBrush(cl);
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
            ringGpu.Value = gl;
            ringGpu.Text = $"{gl:F0}%";
            ringGpu.RingBrush = LoadBrush(gl);
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
        ringRam.Value = s.RamLoad;
        ringRam.Text = $"{s.RamLoad:F0}%";
        ringRam.RingBrush = LoadBrush(s.RamLoad);

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

        // ---- 便签 / 待办 / 专注提醒 ----
        UpdateNotesCard(s.Time);
    }

    private void UpdateWeather()
    {
        if (!Config.Current.ShowWeather)
        {
            cardWeather.Visibility = Visibility.Collapsed;
            UpdateLandscapeSpans();
            return;
        }
        var w = _weather.Current;
        if (w == null) return;
        cardWeather.Visibility = Visibility.Visible;
        UpdateLandscapeSpans();
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
            UpdateLandscapeSpans();
            return;
        }
        cardCodingPlan.Visibility = Visibility.Visible;
        UpdateLandscapeSpans();

        if (_codingPlan.Current.Count == 0)
        {
            txtPlanRefresh.Text = "查询中…";
            return;
        }
        int remain = (int)Math.Ceiling((_codingPlan.LastRefreshUtc + CodingPlanService.RefreshInterval - DateTime.UtcNow).TotalMinutes);
        txtPlanRefresh.Text = remain > 0 ? $"{remain}分钟后刷新" : "刷新中…";

        planRows.Children.Clear();
        bool compact = _orientation == PanelOrientation.Landscape;
        var sections = _codingPlan.Current
            .Select(p => compact ? BuildCompactPlanSection(p) : (FrameworkElement)BuildPlanSection(p))
            .ToList();

        // 横屏:所有供应商横排一行(UniformGrid 等宽),圆环同一高度并排对齐
        if (compact && sections.Count > 1)
        {
            var grid = new UniformGrid { Columns = Math.Min(sections.Count, 4) };
            foreach (var s in sections)
                grid.Children.Add(s);
            planRows.Children.Add(grid);
        }
        else
        {
            foreach (var s in sections)
                planRows.Children.Add(s);
        }
    }

    /// <summary>竖屏供应商区块:标题行(名称+主值) + 每限额窗口一行(标签/用量条/剩余/重置倒计时)。</summary>
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

        // 主值取第一个窗口(绝对值型是余额/点数,百分比型与首窗口剩余一致)
        var main = p.Windows.FirstOrDefault();
        head.Children.Add(new TextBlock
        {
            Text = FormatPlanValue(main, p.Unit),
            FontSize = 22,
            FontWeight = FontWeights.Bold, FontFamily = new FontFamily("Consolas"),
            Foreground = PlanValueBrush(main), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(head);

        var percentWindows = p.Windows.Where(w => w.UsedPercent is not null).ToList();
        foreach (var w in percentWindows)
            panel.Children.Add(BuildPlanWindowRow(w));
        return panel;
    }

    /// <summary>横屏供应商小节(竖版排列):圆环在上(所有供应商同一行同高,DeepSeek 为常满环显示金额),
    /// 名称与限额图例在下。</summary>
    private static FrameworkElement BuildCompactPlanSection(PlanStatus p)
    {
        var cell = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };

        var nameLine = new TextBlock
        {
            FontSize = 12.5, FontWeight = FontWeights.Bold, Foreground = BrushFromHex("#C9D6EE"),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        if (!p.Ok)
        {
            nameLine.Text = $"{p.Name} ✗";
            nameLine.Foreground = BrushFromHex("#F87171");
            nameLine.HorizontalAlignment = HorizontalAlignment.Left;
            cell.Children.Add(nameLine);
            cell.Children.Add(new TextBlock
            {
                Text = p.Error ?? "查询失败", FontSize = 10, Foreground = BrushFromHex("#F87171"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            return cell;
        }

        nameLine.Text = p.PlanName is { Length: > 0 } plan ? $"{p.Name} · {plan}" : p.Name;
        if (p.StaleError is { Length: > 0 })
            nameLine.Text += " · 旧数据";

        var windows = p.Windows.Where(w => w.UsedPercent is not null).ToList();
        NestedRingGauge ring;
        if (windows.Count > 0)
        {
            // 环心显示最紧张的窗口(剩余最少)的剩余百分比
            var tightest = windows.OrderBy(w => 100 - (w.UsedPercent ?? 0)).First();
            ring = new NestedRingGauge
            {
                Width = 66, Height = 66, CenterFontSize = 10.5,
                Rings = windows.Select(w => new RingSlice(w.UsedPercent ?? 0, PlanValueBrush(w))).ToList(),
                CenterText = $"剩{100 - (tightest.UsedPercent ?? 0):F0}%",
                CenterBrush = PlanValueBrush(tightest),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }
        else
        {
            // 绝对值型(DeepSeek 余额):常满环 + 金额居中
            var main = p.Windows.FirstOrDefault();
            ring = new NestedRingGauge
            {
                Width = 66, Height = 66, CenterFontSize = 11.5,
                Rings = [new RingSlice(100, PlanValueBrush(main))],
                CenterText = FormatPlanValue(main, p.Unit),
                CenterBrush = PlanValueBrush(main),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
        }
        cell.Children.Add(ring);
        cell.Children.Add(nameLine);

        foreach (var w in windows)
            cell.Children.Add(BuildCompactLegendLine(w));
        return cell;
    }

    /// <summary>横屏图例行:色点(对应环) + 标签 + 剩余% + 重置倒计时。</summary>
    private static FrameworkElement BuildCompactLegendLine(QuotaWindow w)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        line.Children.Add(new Border
        {
            Width = 8, Height = 8, CornerRadius = new CornerRadius(2.5),
            Background = PlanValueBrush(w), VerticalAlignment = VerticalAlignment.Center,
        });
        var txt = new TextBlock
        {
            FontSize = 11.5, FontFamily = new FontFamily("Consolas"),
            Foreground = BrushFromHex("#55648A"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var remain = new Run($"{w.Label} 剩{100 - (w.UsedPercent ?? 0):F0}%")
        {
            Foreground = PlanValueBrush(w),
            FontWeight = FontWeights.Bold,
        };
        txt.Inlines.Add(remain);
        string reset = FormatResetIn(w.ResetUtc);
        if (reset.Length > 0)
            txt.Inlines.Add(new Run(" " + reset.TrimStart('·', ' ')));
        line.Children.Add(txt);
        return line;
    }

    /// <summary>竖屏窗口行:标签 | 用量条 | 剩余+重置倒计时。</summary>
    private static Grid BuildPlanWindowRow(QuotaWindow w)
    {
        double used = w.UsedPercent ?? 0;
        var row = new Grid { Margin = new Thickness(0, 7, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new TextBlock
        {
            Text = w.Label, FontSize = 11, Foreground = BrushFromHex("#55648A"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        });

        var track = BuildPlanTrack(w, used);
        Grid.SetColumn(track, 1);
        row.Children.Add(track);

        var value = new TextBlock
        {
            Text = FormatPlanValue(w, null, withReset: true),
            FontSize = 12, FontFamily = new FontFamily("Consolas"), Foreground = BrushFromHex("#8FA3C8"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        };
        Grid.SetColumn(value, 2);
        row.Children.Add(value);
        return row;
    }

    /// <summary>用量条:按剩余量着色,宽度跟随轨道实际尺寸。</summary>
    private static Border BuildPlanTrack(QuotaWindow w, double used)
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
        return track;
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

    // ---------- 便签 / 待办 / 专注提醒 ----------

    private string _activeReminderText = "";

    /// <summary>设置窗口改动后刷新卡片(重建待办行 + 立即更新倒计时/横屏便签条)。</summary>
    public void RefreshNotesCard()
    {
        RebuildTodoRows();
        UpdateNotesCard(DateTime.Now);
    }

    /// <summary>每秒刷新:提醒周期到点触发轮换提醒,平时显示倒计时。
    /// 卡片在竖屏挂卡片列表、横屏挂左下大格,本方法只负责内容与可见性。</summary>
    private void UpdateNotesCard(DateTime nowLocal)
    {
        if (!Config.Current.ShowNotes)
        {
            cardNotes.Visibility = Visibility.Collapsed;
            return;
        }
        cardNotes.Visibility = Visibility.Visible;

        var utcNow = DateTime.UtcNow;
        if (_notes.IsReminderDue(utcNow))
        {
            _activeReminderText = _notes.TakeReminder(utcNow);
            _reminderActiveUntilUtc = utcNow.AddSeconds(90);
        }
        bool active = utcNow < _reminderActiveUntilUtc;
        int remainMin = (int)Math.Ceiling((_notes.LastReminderUtc.AddMinutes(_notes.ReminderMinutes) - utcNow).TotalMinutes);
        if (remainMin < 0) remainMin = 0;

        txtNotesCountdown.Text = active ? "提醒中" : $"{remainMin}分钟后提醒";

        txtNoteText.Text = _notes.Note;
        txtNoteText.Visibility = _notes.Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool hasTodos = _notes.Todos.Count > 0;
        txtNotesHint.Text = _notes.Note.Length == 0 && !hasTodos ? "在设置(S)→『便签/专注』里写下便签与待办" : "";
        txtNotesHint.Visibility = txtNotesHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (active)
        {
            rowNotesReminder.Background = BrushFromHex("#3A2B10");
            txtNotesReminder.Text = "⏰ " + _activeReminderText;
            txtNotesReminder.Foreground = BrushFromHex("#FBBF24");
            txtNotesReminder.FontWeight = FontWeights.Bold;
        }
        else
        {
            rowNotesReminder.Background = BrushFromHex("#152340");
            txtNotesReminder.Text = $"💡 {_notes.NextMessagePreview} · {remainMin}分钟后提醒";
            txtNotesReminder.Foreground = BrushFromHex("#8FA3C8");
            txtNotesReminder.FontWeight = FontWeights.Normal;
        }
        rowNotesReminder.Visibility = Visibility.Visible;
    }

    /// <summary>重建待办行:未完成在前,点击整行切换完成态。</summary>
    private void RebuildTodoRows()
    {
        notesTodoPanel.Children.Clear();
        foreach (var t in _notes.Todos.OrderByDescending(t => !t.Done))
            notesTodoPanel.Children.Add(BuildTodoRow(t, _todoFontSize));
    }

    private FrameworkElement BuildTodoRow(TodoItem t, double fontSize)
    {
        var row = new Grid
        {
            Margin = new Thickness(2, 0, 2, 7),
            Background = Brushes.Transparent, // 空白处也可点击
            Cursor = Cursors.Hand,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var box = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = BrushFromHex("#55648A"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (t.Done)
        {
            box.Background = BrushFromHex("#22D3EE");
            box.Child = new TextBlock
            {
                Text = "✓", FontSize = 11, FontWeight = FontWeights.Bold,
                Foreground = BrushFromHex("#0A0F1E"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        }
        row.Children.Add(box);

        var txt = new TextBlock
        {
            Text = t.Text,
            FontSize = fontSize,
            Foreground = BrushFromHex(t.Done ? "#55648A" : "#C9D6EE"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (t.Done)
            txt.TextDecorations = System.Windows.TextDecorations.Strikethrough;
        Grid.SetColumn(txt, 1);
        row.Children.Add(txt);

        row.MouseLeftButtonDown += (_, _) =>
        {
            _notes.ToggleTodo(t);
            RebuildTodoRows();
            UpdateNotesCard(DateTime.Now);
        };
        return row;
    }

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
        > 92 => CachedBrush(0xF8, 0x71, 0x71),
        > 75 => CachedBrush(0xFB, 0xBF, 0x24),
        _ => CachedBrush(0x22, 0xD3, 0xEE),
    };

    private static Brush TempBrush(float t) => t switch
    {
        >= 85 => CachedBrush(0xF8, 0x71, 0x71),
        >= 70 => CachedBrush(0xFB, 0xBF, 0x24),
        >= 50 => CachedBrush(0x67, 0xE8, 0xF9),
        _ => CachedBrush(0x34, 0xD3, 0x99),
    };

    private static readonly Dictionary<string, Brush> _brushCache = new();

    /// <summary>纯色画刷缓存:UpdateUI 每秒多次取刷,冻结后复用避免 GC 压力。</summary>
    private static Brush CachedBrush(byte r, byte g, byte b)
    {
        string key = $"{r:X2}{g:X2}{b:X2}";
        if (!_brushCache.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            _brushCache[key] = brush;
        }
        return brush;
    }

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
