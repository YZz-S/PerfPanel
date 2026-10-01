using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using PerfPanel.Models;

namespace PerfPanel.Services;

/// <summary>LibreHardwareMonitor 数据源:管理员权限下提供温度/风扇/功耗等全套传感器。</summary>
public sealed class HardwareMonitorService : IDisposable
{
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            hardware.Traverse(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsControllerEnabled = false,
        IsStorageEnabled = false,
        IsNetworkEnabled = false,
    };
    private readonly UpdateVisitor _visitor = new();

    private ISensor? _cpuLoad, _cpuTemp, _cpuPower, _cpuFan;
    private ISensor[] _cpuClocks = [];
    private ISensor? _gpuLoad, _gpuTemp, _gpuPower, _gpuFan, _gpuVramUsed, _gpuVramTotal;
    private ISensor? _ramLoad, _ramUsedGb;
    private bool _gpuFanPercent;
    private IHardware? _cpuHw, _gpuHw;   // 懒重选用:部分传感器(如 ADLX 功耗)启动后延迟出现
    private DateTime _lastAcceptUtc;     // LHM 全量遍历限频:传感器值在两次遍历间保持上次读数

    public bool IsAvailable { get; private set; }
    public string CpuName { get; private set; } = "";
    public string GpuName { get; private set; } = "";
    public float RamTotalGb { get; private set; }

    public void Initialize()
    {
        try
        {
            _computer.Open();
            // 两次更新让计数型传感器产生读数
            _computer.Accept(_visitor);
            Thread.Sleep(500);
            _computer.Accept(_visitor);
            PickSensors();
            // 判定可用:任一温度/功耗传感器有真实读数(>0)说明底层驱动工作
            IsAvailable = _cpuLoad?.Value != null || _cpuTemp?.Value > 0 || _gpuTemp?.Value > 0;
            if (!IsAvailable)
            {
                _computer.Close();
            }
        }
        catch
        {
            IsAvailable = false;
            try { _computer.Close(); } catch { }
        }
    }

    private void PickSensors()
    {
        foreach (var hw in _computer.Hardware)
        {
            if (hw.HardwareType == HardwareType.Cpu)
            {
                CpuName = hw.Name;
                _cpuHw ??= hw;
                _cpuTemp ??= Find(hw, SensorType.Temperature,
                    ["CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Package"]);
            }
            else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            {
                GpuName = hw.Name;
            }
            else if (hw.HardwareType == HardwareType.Memory)
            {
                _ramUsedGb ??= Find(hw, SensorType.SmallData, ["Memory Used", "Used Memory"]);
                _ramLoad ??= Find(hw, SensorType.Load, ["Memory"]);
            }
            else if (hw.HardwareType == HardwareType.Motherboard)
            {
                hw.Update();
                foreach (var sub in hw.SubHardware)
                {
                    sub.Update();
                    PickCpuFan(sub);
                }
            }
        }

        // CPU 遍历第二轮:核心温度兜底 + 其余指标
        foreach (var hw in _computer.Hardware)
        {
            if (hw.HardwareType != HardwareType.Cpu) continue;
            _cpuLoad ??= Find(hw, SensorType.Load, ["CPU Total"]);
            _cpuPower ??= Find(hw, SensorType.Power, ["CPU Package", "Package"]);
            // 核心时钟:Intel 命名 "CPU Core #N",AMD 命名 "Core #N";留全量引用,Fill 时取最大值
            _cpuClocks = hw.Sensors
                .Where(s => s.SensorType == SensorType.Clock && CoreClockRegex.IsMatch(s.Name))
                .ToArray();
            _cpuTemp ??= hw.Sensors
                .Where(s => s.SensorType == SensorType.Temperature && s.Value > 0)
                .OrderBy(s => s.Index)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(CpuName)) CpuName = hw.Name;
            break;
        }

        // GPU 指标:双显卡(核显+独显)时优先选有温度传感器的独显,避免名称/占用取核显、温度取独显的混取
        var gpu = _computer.Hardware
            .Where(h => h.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
            .OrderByDescending(h => h.Sensors.Any(s => s.SensorType == SensorType.Temperature))
            .ThenByDescending(h => h.HardwareType == HardwareType.GpuNvidia)
            .FirstOrDefault();
        if (gpu != null)
        {
            _gpuHw = gpu;
            GpuName = gpu.Name;
            _gpuLoad ??= Find(gpu, SensorType.Load, ["GPU Core", "D3D 3D"]);
            _gpuTemp ??= Find(gpu, SensorType.Temperature, ["GPU Core", "GPU Package"]);
            // 各家核显/独显温度名不统一(如 AMD 核显只有 "GPU VR SoC"),按核心/SoC 优先兜底选一路
            _gpuTemp ??= gpu.Sensors
                .Where(s => s.SensorType == SensorType.Temperature)
                .OrderByDescending(s => GpuTempRank(s.Name))
                .ThenBy(s => s.Index)
                .FirstOrDefault();
            _gpuPower = Find(gpu, SensorType.Power, ["GPU Package", "GPU Power", "GPU Board Power", "GPU Core"]) ?? _gpuPower;
            foreach (var s in gpu.Sensors)
            {
                var n = s.Name.ToLowerInvariant();
                if (s.SensorType is SensorType.Fan or SensorType.Control && n.Contains("fan"))
                {
                    _gpuFan ??= s;
                    _gpuFanPercent = s.SensorType == SensorType.Control;
                }
                if (s.SensorType == SensorType.SmallData)
                {
                    if (n.Contains("used")) _gpuVramUsed ??= s;
                    if (n.Contains("total")) _gpuVramTotal ??= s;
                }
            }
        }

        // 内存总量:物理内存传感器(GB)
        foreach (var hw in _computer.Hardware.Where(h => h.HardwareType == HardwareType.Memory))
        {
            var total = Find(hw, SensorType.SmallData, ["Memory Total", "Total Memory", "Memory"]);
            if (total?.Value is > 0)
            {
                RamTotalGb = total.Value.Value;
                break;
            }
        }
    }

    private static ISensor? Find(IHardware hw, SensorType type, params string[] names)
    {
        foreach (var name in names)
        {
            var hit = hw.Sensors.FirstOrDefault(s =>
                s.SensorType == type && s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>核心时钟名:Intel "CPU Core #N" / AMD "Core #N"(排除 Max/有效时钟等衍生名)。</summary>
    private static readonly Regex CoreClockRegex = new(@"^(CPU )?Core #\d+$", RegexOptions.Compiled);

    /// <summary>主板 SuperIO 芯片里挑 CPU 风扇:优先名字含 CPU,其次 Fan #1,再次任何有读数的。</summary>
    private void PickCpuFan(IHardware sub)
    {
        if (_cpuFan != null) return;
        var fans = sub.Sensors.Where(s => s.SensorType == SensorType.Fan).ToList();
        if (fans.Count == 0) return;
        _cpuFan =
            fans.FirstOrDefault(s => s.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase) && s.Value >= 100) ??
            fans.FirstOrDefault(s => s.Name.Equals("Fan #1", StringComparison.OrdinalIgnoreCase)) ??
            fans.FirstOrDefault(s => s.Value >= 100) ??
            fans.FirstOrDefault(s => s.Name.Contains("cpu", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>GPU 温度兜底排序:核心/封装 > SoC > 其他(AMD 核显常只暴露 "GPU VR SoC")。</summary>
    private static int GpuTempRank(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("core") || n.Contains("package")) return 3;
        if (n.Contains("soc")) return 2;
        return 1;
    }

    /// <summary>部分传感器(ADLX 功耗/温度、PawnIO 时钟)在启动后延迟出现,初始化只选一次会永久漏选;
    /// 每次采样前对仍缺失的指标按原规则重选。已选中的不覆盖。</summary>
    private void RePickMissing()
    {
        if (_cpuHw != null)
        {
            _cpuLoad ??= Find(_cpuHw, SensorType.Load, ["CPU Total"]);
            _cpuPower ??= Find(_cpuHw, SensorType.Power, ["CPU Package", "Package"]);
            if (_cpuClocks.Length == 0)
                _cpuClocks = _cpuHw.Sensors
                    .Where(s => s.SensorType == SensorType.Clock && CoreClockRegex.IsMatch(s.Name))
                    .ToArray();
            _cpuTemp ??= _cpuHw.Sensors
                .Where(s => s.SensorType == SensorType.Temperature && s.Value > 0)
                .OrderBy(s => s.Index)
                .FirstOrDefault();
        }

        if (_gpuHw == null) return;
        _gpuLoad ??= Find(_gpuHw, SensorType.Load, ["GPU Core", "D3D 3D"]);
        _gpuTemp ??= Find(_gpuHw, SensorType.Temperature, ["GPU Core", "GPU Package"])
                     ?? _gpuHw.Sensors
                         .Where(s => s.SensorType == SensorType.Temperature)
                         .OrderByDescending(s => GpuTempRank(s.Name))
                         .ThenBy(s => s.Index)
                         .FirstOrDefault();
        _gpuPower ??= Find(_gpuHw, SensorType.Power, ["GPU Package", "GPU Power", "GPU Board Power", "GPU Core"]);
        foreach (var s in _gpuHw.Sensors)
        {
            var n = s.Name.ToLowerInvariant();
            if (s.SensorType is SensorType.Fan or SensorType.Control && n.Contains("fan"))
            {
                _gpuFan ??= s;
                _gpuFanPercent = _gpuFan != null && _gpuFan.SensorType == SensorType.Control;
            }
            if (s.SensorType == SensorType.SmallData)
            {
                if (n.Contains("used")) _gpuVramUsed ??= s;
                if (n.Contains("total")) _gpuVramTotal ??= s;
            }
        }
    }

    public void Fill(SensorSnapshot snap)
    {
        // LHM 遍历(驱动读 MSR/SMN + ADLX 查询)是采样里最贵的一步,限频到 ~1.5s 一次;
        // 间隔内的调用直接复用传感器上次读数(温度/功耗变化本来就慢,曲线无感知)
        var now = DateTime.UtcNow;
        if ((now - _lastAcceptUtc).TotalMilliseconds >= 1500 || _lastAcceptUtc == default)
        {
            _computer.Accept(_visitor);
            _lastAcceptUtc = now;
        }
        RePickMissing();

        snap.CpuName = CpuName;
        snap.GpuName = GpuName;

        snap.CpuLoad = _cpuLoad?.Value;
        snap.CpuTemp = Sanitize(_cpuTemp?.Value);
        snap.GpuTemp = Sanitize(_gpuTemp?.Value);
        // FULL = 至少一路真实温度(管理员 + 驱动 OK),否则降级提示
        // (必须在温度赋值之后判定:快照每次新建,先判恒为 BASIC)
        snap.FullSensorMode = snap.CpuTemp != null || snap.GpuTemp != null;

        snap.CpuPower = Sanitize(_cpuPower?.Value);
        snap.CpuFan = _cpuFan?.Value is >= 100 ? _cpuFan!.Value : null;
        // 多核取最大时钟(睿频时单核领先),LHM 读不出(部分新平台为 NaN)时留给 WMI 回退
        float? maxClock = null;
        foreach (var s in _cpuClocks)
            if (s.Value is > 0 && (maxClock is null || s.Value > maxClock))
                maxClock = s.Value;
        snap.CpuFreq = maxClock is > 0 ? maxClock / 1000f : null;

        snap.GpuLoad = _gpuLoad?.Value;
        snap.GpuPower = Sanitize(_gpuPower?.Value);
        if (_gpuFan?.Value is { } fan)
        {
            bool ok = _gpuFanPercent
                ? fan is > 0 and <= 100
                : fan >= 100; // RPM
            snap.GpuFan = ok ? fan : null;
        }
        snap.GpuFanPercent = _gpuFanPercent;
        // N 卡 LHM 显存单位是 MB:总量 >64 视为 MB,换算 GB
        if (_gpuVramUsed?.Value is { } usedMb && _gpuVramTotal?.Value is { } totalMb)
        {
            if (totalMb > 64)
            {
                snap.GpuVramUsedGb = usedMb / 1024f;
                snap.GpuVramTotalGb = totalMb / 1024f;
            }
            else
            {
                snap.GpuVramUsedGb = usedMb;
                snap.GpuVramTotalGb = totalMb;
            }
        }
        else if (_gpuVramUsed?.Value is { } u)
        {
            snap.GpuVramUsedGb = u > 64 ? u / 1024f : u;
        }

        if (_ramUsedGb?.Value != null)
            snap.RamUsedGb = _ramUsedGb!.Value!.Value;
        if (RamTotalGb > 0)
            snap.RamTotalGb = RamTotalGb;
    }

    /// <summary>温度/功耗物理上不可能为 0,LHM 无管理员时常返回 0,统一清洗为 null。</summary>
    private static float? Sanitize(float? v) => v is > 0 ? v : null;

    public void Dispose()
    {
        try { _computer.Close(); } catch { }
    }
}
