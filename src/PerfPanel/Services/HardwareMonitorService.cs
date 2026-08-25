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

    private ISensor? _cpuLoad, _cpuTemp, _cpuPower, _cpuFan, _cpuClock;
    private ISensor? _gpuLoad, _gpuTemp, _gpuPower, _gpuFan, _gpuVramUsed, _gpuVramTotal;
    private ISensor? _ramLoad, _ramUsedGb;
    private bool _gpuFanPercent;

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
                    _cpuFan ??= Find(sub, SensorType.Fan, ["CPU Fan", "Fan #1", "Fan1"]);
                }
            }
        }

        // CPU 遍历第二轮:核心温度兜底 + 其余指标
        foreach (var hw in _computer.Hardware)
        {
            if (hw.HardwareType != HardwareType.Cpu) continue;
            _cpuLoad ??= Find(hw, SensorType.Load, ["CPU Total"]);
            _cpuPower ??= Find(hw, SensorType.Power, ["CPU Package", "Package"]);
            _cpuClock = hw.Sensors
                .Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("CPU Core") && s.Value > 0)
                .OrderByDescending(s => s.Value)
                .FirstOrDefault();
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
            GpuName = gpu.Name;
            _gpuLoad ??= Find(gpu, SensorType.Load, ["GPU Core", "D3D 3D"]);
            _gpuTemp ??= Find(gpu, SensorType.Temperature, ["GPU Core", "GPU"]);
            _gpuPower = Find(gpu, SensorType.Power, ["GPU Package", "Power", "Board Power", "GPU Power"]) ?? _gpuPower;
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

    public void Fill(SensorSnapshot snap)
    {
        _computer.Accept(_visitor);

        snap.CpuName = CpuName;
        snap.GpuName = GpuName;
        // FULL = 至少一路真实温度(管理员 + 驱动 OK),否则降级提示
        snap.FullSensorMode = snap.CpuTemp != null || snap.GpuTemp != null;

        snap.CpuLoad = _cpuLoad?.Value;
        snap.CpuTemp = Sanitize(_cpuTemp?.Value);
        snap.CpuPower = Sanitize(_cpuPower?.Value);
        snap.CpuFan = _cpuFan?.Value is >= 100 ? _cpuFan!.Value : null;
        snap.CpuFreq = _cpuClock?.Value is > 0 ? _cpuClock!.Value / 1000f : null;

        snap.GpuLoad = _gpuLoad?.Value;
        snap.GpuTemp = Sanitize(_gpuTemp?.Value);
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
