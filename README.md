# PerfPanel — 440×1920 副屏性能监控面板

为竖长条 USB-C 副屏(Windows 视频扩展屏)设计的暗色科技风硬件监控面板,WPF 原生实现。

## 功能

- **时钟区**:实时时间、日期星期、开机时长
- **CPU**:占用率大数字 + 120 秒历史曲线、频率、温度、功耗、风扇转速
- **GPU**:占用率 + 历史曲线、显存占用(带进度条)、温度、功耗、风扇
- **内存**:占用百分比 + 已用/总量
- **网络**:实时上/下行速度 + 下行历史曲线,自动选择活动网卡
- **天气**:Open-Meteo 免费接口(无需 key),自动 IP 定位(可手动改 `weather.json`)
- **智能降级**:无管理员权限时自动隐藏温度/功耗/风扇,右上角显示 `BASIC`;以管理员运行显示 `FULL`

## 运行

```
PerfPanel.exe            # 全屏模式:自动定位 440×1920 副屏(无边框铺满,Esc 退出)
PerfPanel.exe --windowed # 调试模式:主屏窗口化,可随意缩放
```

要求:.NET 8 桌面运行时(WindowsDesktop 8.0.x)。

**要完整温度/风扇/功耗:右键"以管理员身份运行"**(LibreHardwareMonitor 需要 Ring0 驱动读 CPU 传感器;N 卡温度/显存无需管理员也可读)。

## 开机自启

### 普通权限(无 CPU 温度)
`Win+R` → `shell:startup` → 把 `PerfPanel.exe` 的快捷方式放进去。

### 管理员权限(完整传感器,推荐)
用任务计划程序建一个"登录时触发、使用最高权限运行"的任务指向 `PerfPanel.exe`,或导入以下 XML(改路径后):

```xml
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>
  <Actions><Exec><Command>你的路径\PerfPanel.exe</Command></Exec></Actions>
  <Principals><Principal id="Author"><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
</Task>
```

## 天气定位

首次运行自动用 IP 定位并写入 exe 同目录 `weather.json`,想固定城市手动编辑:

```json
{"lat": 39.9042, "lon": 116.4074, "city": "北京"}
```

## 构建

```bash
# 需 .NET 8 SDK
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
# 产物: src/PerfPanel/bin/Release/net8.0-windows/win-x64/publish/PerfPanel.exe(单文件,约 3.7MB)
```

自包含版(免装运行时,约 150MB):
```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## 架构

```
src/PerfPanel/
├── MainWindow.xaml(.cs)        # 440×1920 竖版界面、多屏定位、1s 刷新循环
├── Controls/HistoryGraph.cs    # StreamGeometry 自绘历史曲线(无图表库依赖)
├── Models/SensorSnapshot.cs    # 统一快照模型(字段可空=UI 自动隐藏)
└── Services/
    ├── HardwareMonitorService.cs  # LibreHardwareMonitor:温度/风扇/功耗/频率/显存
    ├── FallbackMonitorService.cs  # 免管理员降级:WMI 本地化安全性能类 + GlobalMemoryStatusEx
    ├── MonitorAggregator.cs       # 双源聚合,字段级互补
    ├── NetworkMonitorService.cs   # 网卡吞吐(粘性选卡)
    ├── WeatherService.cs          # Open-Meteo + ipapi.co,失败静默
    └── MonitorHelper.cs           # EnumDisplayMonitors P/Invoke(不引 WinForms)
```

- 数据源:LibreHardwareMonitorLib 0.9.6(MIT)+ Windows WMI
- 刷新:DispatcherTimer 1 秒,后台线程采样,UI 线程仅渲染
- 实测占用:私有内存约 130MB、工作集约 190MB(WPF + LHM + WMI 常态水平,比浏览器方案省一半)
