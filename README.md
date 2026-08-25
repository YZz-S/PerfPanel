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

### 从源码快速启动(开发调试)

```bash
dotnet run --project src/PerfPanel                # 全屏模式(首次自动还原依赖并编译)
dotnet run --project src/PerfPanel -- --windowed  # 调试模式:主屏窗口化,无副屏时用
```

需 .NET SDK 8.0+(更高版本 SDK 亦可编译 net8.0 目标)。注意 `dotnet run` 是普通权限进程,右上角会显示 `BASIC`(无 CPU 温度/功耗/风扇);想看完整传感器,请用管理员终端执行,或先构建出 exe 再右键管理员运行。

### 运行构建产物

```
PerfPanel.exe            # 全屏模式:自动定位 440×1920 副屏(无边框铺满,Esc 退出)
PerfPanel.exe --windowed # 调试模式:主屏窗口化,可随意缩放
```

要求:.NET 8 桌面运行时(WindowsDesktop 8.0.x)。

**要完整温度/风扇/功耗:右键"以管理员身份运行"**(LibreHardwareMonitor 需要 Ring0 驱动读 CPU 传感器;N 卡温度/显存无需管理员也可读)。

## 开机自启(已内置,命令行一键管理)

```
PerfPanel.exe --autostart-on       # 开启:管理员运行→计划任务(最高权限,完整传感器)
                                   #        普通权限→注册表 Run 键(基础模式)
PerfPanel.exe --autostart-off      # 关闭(两种方式都会清理)
PerfPanel.exe --autostart-status   # 查看当前注册状态
```

最简单的方式:双击 `dist/` 里的现成脚本——
- `开机自启-开启(管理员,推荐).bat`:弹一次 UAC,注册计划任务,开机后完整传感器直接可用
- `开机自启-开启(普通权限).bat`:不弹 UAC,但开机后无 CPU 温度/功耗/风扇
- `开机自启-关闭.bat`

## 天气:位置自定义 + 刷新倒计时

- 面板天气卡片实时显示「城市 · N分钟后刷新」,每 30 分钟自动刷新
- 自定义城市(Open-Meteo 免费地理编码,支持中文/拼音/英文):

```
PerfPanel.exe --set-city 上海
```

或双击 `dist/设置天气城市.bat` 输入城市名;也可直接编辑 exe 旁 `weather.json`:

```json
{"lat": 31.2304, "lon": 121.4737, "city": "上海"}
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

> 仓库不含构建产物。`dist/` 下的 `.bat` 脚本在**脚本所在目录**查找 `PerfPanel.exe`,需把 publish 出的 exe 复制进 `dist/` 后再双击使用(开机自启/设置城市等命令行参数对 `dotnet run` 同样有效,写在 `--` 之后即可)。

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
    ├── WeatherService.cs          # Open-Meteo + 地理编码,位置可自定义,失败静默
    ├── AutostartService.cs        # 开机自启:计划任务/注册表双通道
    └── MonitorHelper.cs           # EnumDisplayMonitors / SetWindowPos P/Invoke(不引 WinForms)
```

改进需求(多传感器/外观自定义/横竖屏/待办等)的设计方案见 [docs/设计方案.md](docs/设计方案.md)。

- 数据源:LibreHardwareMonitorLib 0.9.6(MIT)+ Windows WMI
- 刷新:DispatcherTimer 1 秒,后台线程采样,UI 线程仅渲染
- 实测占用:私有内存约 130MB、工作集约 190MB(WPF + LHM + WMI 常态水平,比浏览器方案省一半)
