using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PerfPanel.Services;

namespace PerfPanel;

/// <summary>图形设置窗口:城市/刷新周期/缩放/显示项/开机自启,改动即时生效并写入 config.json。</summary>
public partial class SettingsWindow : Window
{
    private readonly MainWindow _main;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public SettingsWindow(MainWindow main)
    {
        InitializeComponent();
        Loaded += FitToScreen;
        _main = main;

        var c = Config.Current;
        txtAmapKey.Text = c.AmapKey;
        txtCity.Text = c.City;
        SelectRefresh(c.WeatherRefreshMinutes);
        chkShowWeather.IsChecked = c.ShowWeather;
        chkShowHint.IsChecked = c.ShowHint;
        chkShowFooter.IsChecked = c.ShowFooter;
        txtBrand.Text = c.BrandText;
        chkShowPlan.IsChecked = c.ShowCodingPlan;
        txtDsKey.Text = c.DeepSeekKey;
        txtZpKey.Text = c.ZhipuKey;
        txtVolcAk.Text = c.VolcAk;
        txtVolcSk.Text = c.VolcSk;
        txtMimoCookie.Text = c.MimoCookie;
        txtOcKey.Text = c.OpenCodeKey;
        SelectOrientation(c.Orientation);
        sldScale.Value = c.Scale;
        lblScale.Text = $"{c.Scale * 100:F0}%";
        lblAutoStatus.Text = AutostartService.Status().Replace("\n", " · ");

        // ---- 背景 ----
        lblBgStatus.Text = DescribeBg(c.BackgroundImagePath);
        sldBgOpacity.Value = Math.Clamp(c.BackgroundOpacity * 100, 0, 100);
        lblBgOpacity.Text = $"{(int)Math.Round(sldBgOpacity.Value)}%";
        sldCardOpacity.Value = Math.Clamp(c.CardOpacity * 100, 0, 100);
        lblCardOpacity.Text = $"{(int)Math.Round(sldCardOpacity.Value)}%";

        // ---- 便签 / 专注提醒 ----
        var notes = _main.Notes;
        chkShowNotes.IsChecked = c.ShowNotes;
        chkShowPomodoro.IsChecked = c.ShowPomodoro;
        txtNote.Text = notes.Note;
        txtMessages.Text = string.Join(Environment.NewLine, notes.Messages);
        SelectReminder(notes.ReminderMinutes);
        SelectPomoWork(notes.PomodoroWorkMinutes);
        SelectPomoBreak(notes.PomodoroBreakMinutes);
        ReloadTodoList();

        chkShowNotes.Checked += (_, _) => Apply(c => c.ShowNotes = true);
        chkShowNotes.Unchecked += (_, _) => Apply(c => c.ShowNotes = false);
        chkShowPomodoro.Checked += (_, _) => { Apply(c => c.ShowPomodoro = true); _main.RefreshNotesCard(); };
        chkShowPomodoro.Unchecked += (_, _) => { Apply(c => c.ShowPomodoro = false); _main.RefreshNotesCard(); };

        cmbPomoWork.SelectionChanged += (_, _) =>
        {
            if (cmbPomoWork.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var min))
            {
                _main.Notes.PomodoroWorkMinutes = min;
                _main.Notes.Save();
                _main.Notes.PomoSyncDurations();
                _main.RefreshNotesCard();
            }
        };
        cmbPomoBreak.SelectionChanged += (_, _) =>
        {
            if (cmbPomoBreak.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var min))
            {
                _main.Notes.PomodoroBreakMinutes = min;
                _main.Notes.Save();
                _main.Notes.PomoSyncDurations();
                _main.RefreshNotesCard();
            }
        };

        txtNote.TextChanged += (_, _) =>
        {
            _main.Notes.Note = txtNote.Text;
            _main.Notes.Save();
            _main.RefreshNotesCard();
        };

        btnAddTodo.Click += (_, _) =>
        {
            _main.Notes.AddTodo(txtNewTodo.Text);
            txtNewTodo.Text = "";
            ReloadTodoList();
            _main.RefreshNotesCard();
        };

        cmbReminder.SelectionChanged += (_, _) =>
        {
            if (cmbReminder.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var min))
            {
                _main.Notes.ReminderMinutes = min;
                _main.Notes.Save();
                _main.RefreshNotesCard();
            }
        };

        txtMessages.TextChanged += (_, _) =>
        {
            _main.Notes.Messages = txtMessages.Text
                .Split('\n')
                .Select(l => l.TrimEnd('\r').Trim())
                .Where(l => l.Length > 0)
                .ToList();
            _main.Notes.Save();
            _main.RefreshNotesCard();
        };

        btnSaveKey.Click += async (_, _) =>
        {
            var key = txtAmapKey.Text.Trim();
            if (key.Length == 0)
            {
                Config.Current.AmapKey = "";
                Config.Save();
                lblKeyStatus.Text = "已切换回 Open-Meteo 免费源";
                await _main.Weather.FetchAsync();
                return;
            }

            btnSaveKey.IsEnabled = false;
            try
            {
                lblKeyStatus.Text = "正在测试 Key…";
                var test = await _main.Weather.TestAmapKeyAsync(key);
                if (test.Ok)
                {
                    Config.Current.AmapKey = key;
                    Config.Save();
                    await _main.Weather.FetchAsync();
                }
                lblKeyStatus.Text = test.Msg;
                // 弹框必须带 owner:主面板是 Topmost 全屏窗,无主消息框可能被压在其后形成隐形模态框
                MessageBox.Show(this, test.Msg, "PerfPanel", MessageBoxButton.OK,
                    test.Ok ? MessageBoxImage.Information : MessageBoxImage.Error);
            }
            finally { btnSaveKey.IsEnabled = true; }
        };

        btnSetCity.Click += async (_, _) =>
        {
            var name = txtCity.Text.Trim();
            if (name.Length == 0) return;
            btnSetCity.IsEnabled = false;
            try
            {
                var r = await _main.Weather.SetCityAsync(name);
                txtCity.Text = Config.Current.City;
                MessageBox.Show(this, r.Msg, "PerfPanel", MessageBoxButton.OK,
                    r.Ok ? MessageBoxImage.Information : MessageBoxImage.Error);
            }
            finally { btnSetCity.IsEnabled = true; }
        };

        btnAutoCity.Click += async (_, _) =>
        {
            btnAutoCity.IsEnabled = false;
            try { await _main.Weather.ResetToAutoAsync(); }
            finally { btnAutoCity.IsEnabled = true; }
            txtCity.Text = "";
        };

        cmbRefresh.SelectionChanged += (_, _) =>
        {
            if (cmbRefresh.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var min))
            {
                Config.Current.WeatherRefreshMinutes = min;
                Config.Save();
            }
        };

        cmbOrientation.SelectionChanged += (_, _) =>
        {
            if (cmbOrientation.SelectedItem is ComboBoxItem { Tag: string tag })
            {
                Config.Current.Orientation = tag;
                Config.Save();
                _main.ApplyOrientation();
            }
        };

        chkShowWeather.Checked += (_, _) => Apply(c => c.ShowWeather = true);
        chkShowWeather.Unchecked += (_, _) => Apply(c => c.ShowWeather = false);

        chkShowPlan.Checked += (_, _) => { Apply(c => c.ShowCodingPlan = true); _ = _main.RefreshCodingPlanAsync(); };
        chkShowPlan.Unchecked += (_, _) => Apply(c => c.ShowCodingPlan = false);

        btnDsTest.Click += (_, _) => _ = SaveAndTestPlanAsync(PlanKind.DeepSeek);
        btnZpTest.Click += (_, _) => _ = SaveAndTestPlanAsync(PlanKind.Zhipu);
        btnVolcTest.Click += (_, _) => _ = SaveAndTestPlanAsync(PlanKind.Volc);
        btnMimoTest.Click += (_, _) => _ = SaveAndTestPlanAsync(PlanKind.Mimo);
        btnOcTest.Click += (_, _) => _ = SaveAndTestPlanAsync(PlanKind.OpenCode);

        sldScale.ValueChanged += (_, _) =>
        {
            Config.Current.Scale = Math.Round(sldScale.Value, 2);
            lblScale.Text = $"{Config.Current.Scale * 100:F0}%";
            _main.ApplyScale(Config.Current.Scale);
            Config.Save();
        };

        chkShowHint.Checked += (_, _) => Apply(c => c.ShowHint = true);
        chkShowHint.Unchecked += (_, _) => Apply(c => c.ShowHint = false);

        chkShowFooter.Checked += (_, _) => Apply(c => c.ShowFooter = true);
        chkShowFooter.Unchecked += (_, _) => Apply(c => c.ShowFooter = false);

        // 左上角标题:输入即生效(边打边预览),存 config.json
        txtBrand.TextChanged += (_, _) =>
        {
            Config.Current.BrandText = txtBrand.Text;
            Config.Save();
            _main.ApplyConfig();
        };

        // ---- 背景:选图复制到 exe 旁、清除、两根透明度滑杆即时预览 ----
        btnPickBg.Click += async (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择背景图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) != true) return;
            btnPickBg.IsEnabled = false;
            try
            {
                string stored = await Task.Run(() => StoreBackgroundImage(dlg.FileName));
                Config.Current.BackgroundImagePath = stored;
                Config.Save();
                _main.ApplyConfig();
                lblBgStatus.Text = DescribeBg(stored);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"背景设置失败:{ex.Message}", "PerfPanel",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { btnPickBg.IsEnabled = true; }
        };

        btnClearBg.Click += (_, _) =>
        {
            string old = Config.Current.BackgroundImagePath;
            Config.Current.BackgroundImagePath = "";
            Config.Save();
            _main.ApplyConfig();
            lblBgStatus.Text = DescribeBg("");
            // 只清理本应用生成的 exe 旁副本,用户源图不动
            string baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            if (old.Length > 0 && Path.GetDirectoryName(old) == baseDir)
                try { File.Delete(old); } catch { }
        };

        sldBgOpacity.ValueChanged += (_, _) =>
        {
            Config.Current.BackgroundOpacity = Math.Round(sldBgOpacity.Value / 100.0, 2);
            lblBgOpacity.Text = $"{(int)Math.Round(sldBgOpacity.Value)}%";
            Config.Save();
            _main.ApplyBackgroundOpacity();
        };

        sldCardOpacity.ValueChanged += (_, _) =>
        {
            Config.Current.CardOpacity = Math.Round(sldCardOpacity.Value / 100.0, 2);
            lblCardOpacity.Text = $"{(int)Math.Round(sldCardOpacity.Value)}%";
            Config.Save();
            _main.ApplyBackgroundOpacity();
        };

        btnAutoOn.Click += (_, _) =>
        {
            var (ok, msg) = AutostartService.Enable();
            MessageBox.Show(this, msg, "PerfPanel", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Error);
            lblAutoStatus.Text = AutostartService.Status().Replace("\n", " · ");
        };
        btnAutoOff.Click += (_, _) =>
        {
            AutostartService.Disable();
            lblAutoStatus.Text = AutostartService.Status().Replace("\n", " · ");
        };

        btnClose.Click += (_, _) => Close();

        // 城市定位是异步的,定时刷新状态行
        _statusTimer.Tick += (_, _) => UpdateCityStatus();
        _statusTimer.Start();
        UpdateCityStatus();
    }

    private void Apply(Action<AppConfig> change)
    {
        change(Config.Current);
        Config.Save();
        _main.ApplyConfig();
    }

    // ---- 背景图 ----

    private static readonly string[] BgExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    /// <summary>状态行文字:显示当前背景图路径,文件缺失时给出回退提示。</summary>
    private static string DescribeBg(string path)
    {
        if (string.IsNullOrEmpty(path)) return "未设置(默认深色底)";
        return File.Exists(path) ? path : $"{path}(文件缺失,已回退默认底色)";
    }

    /// <summary>把所选图片复制到 exe 旁(bg.<原扩展名>,清掉旧扩展名的残留副本,背景随程序走),返回副本路径。</summary>
    private static string StoreBackgroundImage(string srcPath)
    {
        var ext = Path.GetExtension(srcPath).ToLowerInvariant();
        if (!BgExtensions.Contains(ext)) ext = ".png"; // 无法识别的格式按内容嗅探解码,统一落到 bg.png
        string dest = Path.Combine(AppContext.BaseDirectory, "bg" + ext);
        foreach (var e in BgExtensions)
        {
            string old = Path.Combine(AppContext.BaseDirectory, "bg" + e);
            if (!string.Equals(old, dest, StringComparison.OrdinalIgnoreCase) && File.Exists(old))
                File.Delete(old);
        }
        File.Copy(srcPath, dest, overwrite: true);
        return dest;
    }

    private enum PlanKind { DeepSeek, Zhipu, Volc, Mimo, OpenCode }

    /// <summary>保存密钥到 config.json 并即时查询一次;留空保存 = 清除该项(面板不再查询)。</summary>
    private async Task SaveAndTestPlanAsync(PlanKind kind)
    {
        var c = Config.Current;
        string key = "", ak = "", sk = "";
        Button btn;
        switch (kind)
        {
            case PlanKind.DeepSeek: key = txtDsKey.Text.Trim(); c.DeepSeekKey = key; btn = btnDsTest; break;
            case PlanKind.Zhipu: key = txtZpKey.Text.Trim(); c.ZhipuKey = key; btn = btnZpTest; break;
            case PlanKind.Mimo: key = txtMimoCookie.Text.Trim(); c.MimoCookie = key; btn = btnMimoTest; break;
            case PlanKind.OpenCode: key = txtOcKey.Text.Trim(); c.OpenCodeKey = key; btn = btnOcTest; break;
            default: ak = txtVolcAk.Text.Trim(); sk = txtVolcSk.Text.Trim(); c.VolcAk = ak; c.VolcSk = sk; btn = btnVolcTest; break;
        }
        Config.Save();

        bool empty = kind switch
        {
            PlanKind.Volc => ak.Length == 0 || sk.Length == 0,
            PlanKind.Mimo => key.Length == 0,
            _ => key.Length == 0,
        };
        if (empty)
        {
            await _main.RefreshCodingPlanAsync();
            MessageBox.Show(this, "已保存(留空 = 面板不再查询该项)", "PerfPanel",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        btn.IsEnabled = false;
        try
        {
            PlanStatus r = kind switch
            {
                PlanKind.DeepSeek => await _main.CodingPlan.QueryDeepSeekAsync(key),
                PlanKind.Zhipu => await _main.CodingPlan.QueryZhipuAsync(key),
                PlanKind.Mimo => await _main.CodingPlan.QueryMimoAsync(key),
                PlanKind.OpenCode => await _main.CodingPlan.QueryOpenCodeAsync(key),
                _ => await _main.CodingPlan.QueryVolcAsync(ak, sk),
            };
            string msg = r.Ok
                ? "✓ 查询成功\n" + string.Join("\n", r.Windows.Select(FormatPlanTestLine))
                : "✗ " + r.Error;
            await _main.RefreshCodingPlanAsync();
            // 弹框必须带 owner:主面板是 Topmost 全屏窗(同高德 Key)
            MessageBox.Show(this, msg, "PerfPanel", MessageBoxButton.OK,
                r.Ok ? MessageBoxImage.Information : MessageBoxImage.Error);
        }
        finally { btn.IsEnabled = true; }
    }

    private static string FormatPlanTestLine(QuotaWindow w)
    {
        if (w.UsedPercent is null && w.RemainingValue is { } abs)
            return $"{w.Label}:{abs:0.##}";
        string reset = w.ResetUtc is { } r
            ? $"({FormatResetIn(r)})" : "";
        return $"{w.Label}窗 已用 {w.UsedPercent:F0}% {reset}".TrimEnd();
    }

    private static string FormatResetIn(DateTime resetUtc)
    {
        var span = resetUtc - DateTime.UtcNow;
        if (span <= TimeSpan.Zero) return "即将重置";
        if (span.TotalMinutes < 60) return $"{(int)Math.Ceiling(span.TotalMinutes)}分钟后重置";
        if (span.TotalHours < 48) return $"{span.TotalHours:F1}小时后重置";
        return $"{span.TotalDays:F1}天后重置";
    }

    private void UpdateCityStatus()
    {
        string? city = _main.Weather.Current?.City;
        lblCityStatus.Text = string.IsNullOrEmpty(city)
            ? (string.IsNullOrEmpty(Config.Current.City) ? "当前位置:自动定位中…" : $"当前位置:{Config.Current.City}")
            : $"当前位置:{city} · {(Config.Current.City.Length > 0 ? "手动" : "自动")}";
    }

    private void SelectRefresh(int minutes)
    {
        foreach (ComboBoxItem item in cmbRefresh.Items)
        {
            if (item.Tag is string tag && int.Parse(tag) == minutes)
            {
                cmbRefresh.SelectedItem = item;
                return;
            }
        }
        cmbRefresh.SelectedIndex = 2; // 默认 30
    }

    private void SelectOrientation(string mode)
    {
        foreach (ComboBoxItem item in cmbOrientation.Items)
        {
            if (item.Tag is string tag && tag == mode)
            {
                cmbOrientation.SelectedItem = item;
                return;
            }
        }
        cmbOrientation.SelectedIndex = 0; // 默认 auto
    }

    private void SelectReminder(int minutes)
    {
        foreach (ComboBoxItem item in cmbReminder.Items)
        {
            if (item.Tag is string tag && int.Parse(tag) == minutes)
            {
                cmbReminder.SelectedItem = item;
                return;
            }
        }
        cmbReminder.SelectedIndex = 1; // 默认 30
    }

    private void SelectPomoWork(int minutes)
    {
        foreach (ComboBoxItem item in cmbPomoWork.Items)
        {
            if (item.Tag is string tag && int.Parse(tag) == minutes)
            {
                cmbPomoWork.SelectedItem = item;
                return;
            }
        }
        cmbPomoWork.SelectedIndex = 1; // 默认 25
    }

    private void SelectPomoBreak(int minutes)
    {
        foreach (ComboBoxItem item in cmbPomoBreak.Items)
        {
            if (item.Tag is string tag && int.Parse(tag) == minutes)
            {
                cmbPomoBreak.SelectedItem = item;
                return;
            }
        }
        cmbPomoBreak.SelectedIndex = 1; // 默认 5
    }

    /// <summary>重建待办列表:勾选框切换完成、右侧删除。</summary>
    private void ReloadTodoList()
    {
        todoList.Children.Clear();
        foreach (var t in _main.Notes.Todos.ToList())
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = System.Windows.GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = System.Windows.GridLength.Auto });

            var chk = new CheckBox
            {
                IsChecked = t.Done,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            chk.Checked += (_, _) => { _main.Notes.ToggleTodo(t); _main.RefreshNotesCard(); };
            chk.Unchecked += (_, _) => { _main.Notes.ToggleTodo(t); _main.RefreshNotesCard(); };
            row.Children.Add(chk);

            var label = new TextBlock
            {
                Text = t.Text,
                FontSize = 13,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xD6, 0xEE)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (t.Done)
                label.TextDecorations = System.Windows.TextDecorations.Strikethrough;
            System.Windows.Controls.Grid.SetColumn(label, 1);
            row.Children.Add(label);

            var del = new Button
            {
                Content = "删除",
                Style = (Style)Resources["BtnStyle"],
                FontSize = 11,
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(6, 0, 0, 0),
            };
            del.Click += (_, _) =>
            {
                _main.Notes.RemoveTodo(t);
                ReloadTodoList();
                _main.RefreshNotesCard();
            };
            System.Windows.Controls.Grid.SetColumn(del, 2);
            row.Children.Add(del);

            todoList.Children.Add(row);
        }
    }

    /// <summary>按窗口所在屏幕的工作区收口尺寸:小屏(如 1920×440 副屏)下不超出屏幕,超出部分页内滚动。
    /// 挂在 Loaded 而非 SourceInitialized:此时 CenterScreen 定位已完成,MonitorFromWindow 才能判对显示器。</summary>
    private void FitToScreen(object sender, RoutedEventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var area = MonitorHelper.GetWorkAreaDip(hwnd);
            if (area is { } a)
            {
                if (Height > a.Height - 24) Height = Math.Max(420, a.Height - 24);
                if (Width > a.Width - 24) Width = Math.Max(460, a.Width - 24);
            }
        }
        catch { /* 屏幕信息取不到时保持默认尺寸 */ }
    }

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        base.OnClosed(e);
    }
}
