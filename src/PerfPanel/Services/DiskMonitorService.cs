using PerfPanel.Models;

namespace PerfPanel.Services;

/// <summary>固定磁盘分区监控:容量走 WMI Win32_LogicalDisk(30s 缓存,免管理员也可用);
/// 卷↔物理盘映射(SMBIOS 关联查询,仅启动时一次)把 LHM 存储温度标注到对应卷;映射失败只丢温度不丢容量。</summary>
public sealed class DiskMonitorService
{
    private List<(string Drive, double UsedGb, double TotalGb)> _volumes = [];
    private readonly Dictionary<string, string> _modelByVolume = new();   // "C:" → 物理盘型号
    private bool _mappingTried;
    private DateTime _nextWmiUtc = DateTime.MinValue;

    /// <summary>卷列表(系统盘最前,其余按盘符);容量 30s 刷新,其余复用缓存。
    /// hms 传 LHM(供存储温度);LHM 不可用时温度为 null。</summary>
    public List<DiskVolumeInfo> Sample(HardwareMonitorService? hms)
    {
        var now = DateTime.UtcNow;
        if (_volumes.Count == 0 || now >= _nextWmiUtc)
        {
            _nextWmiUtc = now.AddSeconds(30);
            _volumes = QueryVolumes();
        }
        if (!_mappingTried)
        {
            _mappingTried = true;
            BuildMapping();
        }

        var temps = hms?.StorageTemps;
        var result = new List<DiskVolumeInfo>(_volumes.Count);
        foreach (var (drive, used, total) in _volumes)
        {
            float? temp = null;
            if (_modelByVolume.TryGetValue(drive, out var model) && temps != null)
                foreach (var (m, t) in temps)
                    if (t is > 0 && ModelsMatch(m, model)) { temp = t; break; }
            result.Add(new DiskVolumeInfo(drive, used, total, temp));
        }
        return result;
    }

    private static List<(string, double, double)> QueryVolumes()
    {
        var list = new List<(string, double, double)>();
        try
        {
            using var q = new System.Management.ManagementObjectSearcher(
                "SELECT DeviceID, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType=3");
            foreach (var mo in q.Get())
            {
                string drive = Convert.ToString(mo["DeviceID"]) ?? "";
                double total = Convert.ToDouble(mo["Size"] ?? 0);
                double free = Convert.ToDouble(mo["FreeSpace"] ?? 0);
                if (drive.Length == 0 || total <= 0) continue;
                list.Add((drive, (total - free) / 1073741824.0, total / 1073741824.0));
            }
        }
        catch
        {
            // WMI 不可用:返回空列表,面板隐藏磁盘卡
        }
        return list.OrderBy(v => v.Item1 != "C:")
                   .ThenBy(v => v.Item1, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    /// <summary>卷↔物理盘型号:LogicalDiskToPartition + DiskDriveToDiskPartition + DiskDrive,启动一次(静态映射)。</summary>
    private void BuildMapping()
    {
        try
        {
            var diskModels = new Dictionary<string, string>();
            using var drives = new System.Management.ManagementObjectSearcher("SELECT DeviceID, Model FROM Win32_DiskDrive");
            foreach (var d in drives.Get())
                diskModels[Convert.ToString(d["DeviceID"]) ?? ""] = Convert.ToString(d["Model"]) ?? "";

            var partToDrive = new Dictionary<string, string>();   // 分区路径 → 物理盘路径
            using var dd = new System.Management.ManagementObjectSearcher(
                "SELECT Antecedent, Dependent FROM Win32_DiskDriveToDiskPartition");
            foreach (var r in dd.Get())
                partToDrive[KeyOf(r, "Dependent")] = KeyOf(r, "Antecedent");

            var volToPart = new Dictionary<string, string>();     // 逻辑盘路径 → 分区路径
            using var lp = new System.Management.ManagementObjectSearcher(
                "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition");
            foreach (var r in lp.Get())
                volToPart[KeyOf(r, "Dependent")] = KeyOf(r, "Antecedent");

            foreach (var (volPath, partPath) in volToPart)
            {
                if (!partToDrive.TryGetValue(partPath, out var drivePath)) continue;
                if (diskModels.TryGetValue(DeviceKey(drivePath), out var model) && model.Length > 0)
                    _modelByVolume[DeviceKey(volPath)] = model;
            }
        }
        catch
        {
            // 关联查询失败:温度不标注,容量不受影响
        }
    }

    /// <summary>WMI 关联对象路径里抽 DeviceID 值(形如 …Win32_LogicalDisk.DeviceID="C:")。</summary>
    private static string DeviceKey(string path)
    {
        int i = path.IndexOf("=\"", StringComparison.Ordinal);
        return i < 0 ? "" : path[(i + 2)..].TrimEnd('"');
    }

    private static string KeyOf(System.Management.ManagementBaseObject row, string field) =>
        row[field] as string ?? "";

    /// <summary>LHM 存储名与 Win32_DiskDrive.Model 匹配(同为型号串;归一化空白大小写,双向包含兜底——
    /// 同型号不同容量互不包含,不会误配)。</summary>
    private static bool ModelsMatch(string lhmName, string wmiModel)
    {
        string a = Norm(lhmName), b = Norm(wmiModel);
        if (a.Length == 0 || b.Length == 0) return false;
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
               a.Contains(b, StringComparison.OrdinalIgnoreCase) ||
               b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static string Norm(string s) =>
        string.Concat(s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
