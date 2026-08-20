using PerfPanel.Models;
using PerfPanel.Services;

namespace PerfPanel.Services;

/// <summary>聚合 LHM 与免管理员降级源:任一来源缺失的字段自动用另一个补。</summary>
public sealed class MonitorAggregator : IDisposable
{
    private readonly HardwareMonitorService _lhm = new();
    private readonly FallbackMonitorService _fallback = new();
    private readonly NetworkMonitorService _net = new();
    private readonly object _lock = new();

    public bool FullMode => _lhm.IsAvailable;

    public void Initialize()
    {
        lock (_lock)
        {
            _lhm.Initialize();
            _fallback.Initialize();
        }
    }

    public SensorSnapshot Sample()
    {
        lock (_lock)
        {
            return SampleCore();
        }
    }

    private SensorSnapshot SampleCore()
    {
        var snap = new SensorSnapshot();
        if (_lhm.IsAvailable)
        {
            _lhm.Fill(snap);
        }

        if (snap.CpuLoad == null) snap.CpuLoad = _fallback.GetCpuLoad();
        if (snap.CpuFreq == null) snap.CpuFreq = _fallback.GetCpuFreqGHz();
        if (snap.GpuLoad == null) snap.GpuLoad = _fallback.GetGpuLoad();
        if (snap.GpuVramUsedGb == null) snap.GpuVramUsedGb = _fallback.GetGpuVramUsedGb();
        if (string.IsNullOrEmpty(snap.CpuName)) snap.CpuName = _fallback.CpuName;
        if (string.IsNullOrEmpty(snap.GpuName)) snap.GpuName = _fallback.GpuName;

        if (snap.RamTotalGb <= 0) snap.RamTotalGb = _fallback.RamTotalGb;
        if (!_lhm.IsAvailable || snap.RamUsedGb <= 0)
            snap.RamUsedGb = _fallback.GetRamUsedGb();

        var (down, up, name) = _net.Sample();
        snap.NetDownBps = down;
        snap.NetUpBps = up;
        snap.NetInterface = name;

        if (!_lhm.IsAvailable)
            snap.SourceNote = "BASIC MODE · 以管理员运行可显示温度/风扇/功耗";
        return snap;
    }

    public void Dispose() => _lhm.Dispose();
}
