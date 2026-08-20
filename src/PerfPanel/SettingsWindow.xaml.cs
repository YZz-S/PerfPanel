using System.Windows;
using System.Windows.Controls;
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
        _main = main;

        var c = Config.Current;
        txtAmapKey.Text = c.AmapKey;
        txtCity.Text = c.City;
        SelectRefresh(c.WeatherRefreshMinutes);
        chkShowWeather.IsChecked = c.ShowWeather;
        chkShowHint.IsChecked = c.ShowHint;
        sldScale.Value = c.Scale;
        lblScale.Text = $"{c.Scale * 100:F0}%";
        lblAutoStatus.Text = AutostartService.Status().Replace("\n", " · ");

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
            lblKeyStatus.Text = "正在测试 Key…";
            var test = await _main.Weather.TestAmapKeyAsync(key);
            if (test.Ok)
            {
                Config.Current.AmapKey = key;
                Config.Save();
                await _main.Weather.FetchAsync();
            }
            lblKeyStatus.Text = test.Msg;
            btnSaveKey.IsEnabled = true;
        };

        btnSetCity.Click += async (_, _) =>
        {
            var name = txtCity.Text.Trim();
            if (name.Length == 0) return;
            btnSetCity.IsEnabled = false;
            var r = await _main.Weather.SetCityAsync(name);
            txtCity.Text = Config.Current.City;
            MessageBox.Show(r.Msg, "PerfPanel", MessageBoxButton.OK,
                r.Ok ? MessageBoxImage.Information : MessageBoxImage.Error);
            btnSetCity.IsEnabled = true;
        };

        btnAutoCity.Click += async (_, _) =>
        {
            btnAutoCity.IsEnabled = false;
            await _main.Weather.ResetToAutoAsync();
            txtCity.Text = "";
            btnAutoCity.IsEnabled = true;
        };

        cmbRefresh.SelectionChanged += (_, _) =>
        {
            if (cmbRefresh.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var min))
            {
                Config.Current.WeatherRefreshMinutes = min;
                Config.Save();
            }
        };

        chkShowWeather.Checked += (_, _) => Apply(c => c.ShowWeather = true);
        chkShowWeather.Unchecked += (_, _) => Apply(c => c.ShowWeather = false);

        sldScale.ValueChanged += (_, _) =>
        {
            Config.Current.Scale = Math.Round(sldScale.Value, 2);
            lblScale.Text = $"{Config.Current.Scale * 100:F0}%";
            _main.ApplyScale(Config.Current.Scale);
            Config.Save();
        };

        chkShowHint.Checked += (_, _) => Apply(c => c.ShowHint = true);
        chkShowHint.Unchecked += (_, _) => Apply(c => c.ShowHint = false);

        btnAutoOn.Click += (_, _) =>
        {
            var (ok, msg) = AutostartService.Enable();
            MessageBox.Show(msg, "PerfPanel", MessageBoxButton.OK,
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

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer.Stop();
        base.OnClosed(e);
    }
}
