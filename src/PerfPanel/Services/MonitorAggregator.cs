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

    /// <summary>Initialize 是否已完成(LHM 枚举是后台异步的,完成前 GPU 列表为空属正常)。</summary>
    public bool Ready { get; private set; }

    /// <summary>LHM 枚举到的全部 GPU 名称(LHM 未就绪时为空)。</summary>
    public IReadOnlyList<string> GpuNames => _lhm.GpuNames;

    /// <summary>当前监控的 GPU 序号(-1 = 自动)。</summary>
    public int SelectedGpuIndex => _lhm.SelectedGpuIndex;

    /// <summary>切换监控的 GPU(-1 = 自动);与采样共用锁,避免切换瞬间读到半新半旧状态。</summary>
    public bool SelectGpu(int index)
    {
        lock (_lock)
        {
            return _lhm.SelectGpu(index);
        }
    }

    public void Initialize()
    {
        lock (_lock)
        {
            _lhm.Initialize();
            _fallback.Initialize();
            Ready = true;
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
        // 用户显式选定了 GPU 时,GPU 类指标只信 LHM 对该卡的数据;
        // 回退源(全系统聚合最大值)会把另一块卡的数据混进来,只在自动模式补
        bool gpuExplicit = _lhm.IsAvailable && _lhm.SelectedGpuIndex >= 0;
        if (snap.GpuLoad == null && !gpuExplicit) snap.GpuLoad = _fallback.GetGpuLoad();
        if (snap.GpuVramUsedGb == null && !gpuExplicit) snap.GpuVramUsedGb = _fallback.GetGpuVramUsedGb();
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
