using System.Net.NetworkInformation;

namespace PerfPanel.Services;

/// <summary>网卡吞吐监控:自动选择活动物理网卡(粘性),字节差分换算 B/s。</summary>
public sealed class NetworkMonitorService
{
    private sealed record NicSample(string Id, string Name, long Down, long Up, int Kind);

    private readonly Dictionary<string, (long rx, long tx)> _last = new();
    private string? _preferredId;

    public (long downBps, long upBps, string ifName) Sample()
    {
        var samples = new List<NicSample>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                or NetworkInterfaceType.Tunnel) continue;
            if (nic.OperationalStatus != OperationalStatus.Up) continue;

            var desc = nic.Description;
            if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
                desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)) continue;

            var stats = nic.GetIPStatistics();
            if (stats == null) continue;

            long rx = stats.BytesReceived, tx = stats.BytesSent;
            if (!_last.TryGetValue(nic.Id, out var prev))
            {
                _last[nic.Id] = (rx, tx);
                continue;
            }
            _last[nic.Id] = (rx, tx);

            long down = Math.Max(rx - prev.rx, 0);
            long up = Math.Max(tx - prev.tx, 0);
            int kind = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                or NetworkInterfaceType.Wireless80211 ? 2 : 1;
            samples.Add(new NicSample(nic.Id, desc, down, up, kind));
        }

        // 粘性:当前网卡继续用,除非它没了流量而别的网卡有(>2KB/s)
        var preferred = samples.FirstOrDefault(s => s.Id == _preferredId);
        if (preferred != null)
        {
            bool idle = preferred.Down + preferred.Up < 2048;
            bool otherBusy = samples.Any(s => s.Id != _preferredId && s.Down + s.Up > 2048);
            if (!idle || !otherBusy)
                return (preferred.Down, preferred.Up, TrimName(preferred.Name));
        }

        var best = samples
            .OrderByDescending(s => s.Down + s.Up > 2048)
            .ThenByDescending(s => s.Kind)
            .FirstOrDefault();
        if (best == null) return (0, 0, "");
        _preferredId = best.Id;
        return (best.Down, best.Up, TrimName(best.Name));
    }

    private static string TrimName(string name) =>
        name.Length > 28 ? name[..28] + "…" : name;
}
