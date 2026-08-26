using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PerfPanel.Services;

/// <summary>免管理员的基础数据源:CPU 占用走 PDH 计数器(与任务管理器同口径),内存 P/Invoke、其余走本地化安全的 WMI 格式化性能类。</summary>
public sealed class FallbackMonitorService : IDisposable
{
    private ulong _totalPhysMb;
    private float _cpuMaxClockGHz;
    private string _cpuName = "";
    private string _gpuName = "";
    private bool _wmiOk;
    private PerformanceCounter? _cpuCounter;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public void Initialize()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms))
        {
            _totalPhysMb = ms.ullTotalPhys / (1024 * 1024);
        }

        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Name, MaxClockSpeed FROM Win32_Processor");
            foreach (var mo in searcher.Get())
            {
                _cpuName = (mo["Name"] as string ?? "").Trim();
                _cpuMaxClockGHz = Convert.ToSingle(mo["MaxClockSpeed"]) / 1000f;
                break;
            }

            using var gpu = new System.Management.ManagementObjectSearcher(
                "SELECT Name FROM Win32_VideoController WHERE AdapterDACType IS NOT NULL OR ConfigManagerErrorCode = 0");
            foreach (var mo in gpu.Get())
            {
                var n = (mo["Name"] as string ?? "").Trim();
                if (!string.IsNullOrEmpty(n) && !n.Contains("Basic Render", StringComparison.OrdinalIgnoreCase))
                {
                    _gpuName = n;
                    break;
                }
            }
            _wmiOk = true;
        }
        catch
        {
            _wmiOk = false; // WMI 异常时仅内存/网络可用
        }

        // CPU 占用优先用 PDH 计数器:相邻两次 NextValue 之间做差分,口径与任务管理器一致,
        // 且比每秒一次 WMI 查询便宜得多。首次调用只建立基线(返回 0),这里先预热。
        try
        {
            _cpuCounter = new PerformanceCounter("Processor Information", "% Processor Time", "_Total", readOnly: true);
            _cpuCounter.NextValue();
        }
        catch
        {
            _cpuCounter?.Dispose();
            _cpuCounter = null; // 计数器库不可用时 GetCpuLoad 回退 WMI
        }
    }

    public string CpuName => _cpuName;
    public string GpuName => _gpuName;
    public float RamTotalGb => _totalPhysMb / 1024f;

    public float GetRamUsedGb()
    {
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref ms) || ms.ullTotalPhys == 0) return 0;
        return (ms.ullTotalPhys - ms.ullAvailPhys) / (1024f * 1024f * 1024f);
    }

    /// <summary>CPU 总占用(%):PDH 计数器差分(同任务管理器口径),不可用时回退本地化无关的 WMI 格式化类。</summary>
    public float? GetCpuLoad()
    {
        if (_cpuCounter != null)
        {
            try
            {
                // PDH 偶发返回 101%/-1% 之类的抖动值,夹回 0~100
                return Math.Clamp(_cpuCounter.NextValue(), 0f, 100f);
            }
            catch
            {
                _cpuCounter.Dispose();
                _cpuCounter = null; // 运行中失效则降级,后续走 WMI 路径
            }
        }
        return GetCpuLoadByWmi();
    }

    private float? GetCpuLoadByWmi()
    {
        if (!_wmiOk) return null;
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name = '_Total'");
            foreach (var mo in s.Get())
                return Convert.ToSingle(mo["PercentProcessorTime"]);
        }
        catch { }
        return null;
    }

    /// <summary>CPU 有效频率(GHz):PercentProcessorPerformance × 标称频率,近似值。</summary>
    public float? GetCpuFreqGHz()
    {
        if (!_wmiOk || _cpuMaxClockGHz <= 0) return null;
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name = '_Total'");
            foreach (var mo in s.Get())
            {
                var pct = Convert.ToSingle(mo["PercentProcessorPerformance"]);
                return _cpuMaxClockGHz * Math.Min(pct, 100f) / 100f;
            }
        }
        catch { }
        return null;
    }

    /// <summary>GPU 占用(%):GPU 引擎性能类里 engtype_3D 实例取最大值。</summary>
    public float? GetGpuLoad()
    {
        if (!_wmiOk) return null;
        try
        {
            float best = -1;
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT Name, PercentUtilization FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
            foreach (var mo in s.Get())
            {
                var name = mo["Name"] as string ?? "";
                if (!name.Contains("engtype_3D")) continue;
                var v = Convert.ToSingle(mo["PercentUtilization"]);
                if (v > best) best = v;
            }
            return best >= 0 ? best : null;
        }
        catch { }
        return null;
    }

    /// <summary>显存占用(GB):GPU 适配器内存类的专用内存求和。</summary>
    public float? GetGpuVramUsedGb()
    {
        if (!_wmiOk) return null;
        try
        {
            double sum = 0;
            using var s = new System.Management.ManagementObjectSearcher(
                "SELECT DedicatedUsage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory");
            foreach (var mo in s.Get())
                sum += Convert.ToDouble(mo["DedicatedUsage"]);
            return (float)(sum / (1024.0 * 1024.0 * 1024.0));
        }
        catch { }
        return null;
    }

    public void Dispose() => _cpuCounter?.Dispose();
}
