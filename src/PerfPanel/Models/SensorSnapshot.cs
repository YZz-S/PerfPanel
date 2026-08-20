namespace PerfPanel.Models;

/// <summary>每秒采样后的统一快照。所有字段可空:采集不到时 UI 隐藏对应行。</summary>
public class SensorSnapshot
{
    // 系统
    public DateTime Time = DateTime.Now;
    public TimeSpan Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
    public string CpuName = "";
    public string GpuName = "";
    public bool FullSensorMode;          // true = LHM 内核驱动可用(管理员)
    public string? SourceNote;           // 降级原因说明

    // CPU
    public float? CpuLoad;               // %
    public float? CpuTemp;               // °C
    public float? CpuFreq;               // GHz(平均有效频率)
    public float? CpuPower;              // W
    public float? CpuFan;                // RPM

    // GPU
    public float? GpuLoad;               // %
    public float? GpuTemp;               // °C
    public float? GpuPower;              // W
    public float? GpuFan;                // RPM 或 %(见 GpuFanPercent)
    public bool GpuFanPercent;           // N 卡风扇读数是百分比
    public float? GpuVramUsedGb;
    public float? GpuVramTotalGb;

    // 内存
    public float RamTotalGb;
    public float RamUsedGb;
    public float RamLoad => RamTotalGb > 0 ? RamUsedGb / RamTotalGb * 100f : 0f;

    // 网络
    public long NetDownBps;              // 字节/秒
    public long NetUpBps;
    public string NetInterface = "";
}
