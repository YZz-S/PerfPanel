using System.Runtime.InteropServices;

namespace PerfPanel.Services;

/// <summary>多显示器枚举(user32 P/Invoke,避免为 Screen.AllScreens 引入整个 WinForms)。</summary>
internal static class MonitorHelper
{
    public sealed record MonitorBounds(int Left, int Top, int Width, int Height, bool Primary);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool EnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, EnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    public static List<MonitorBounds> GetAll()
    {
        var list = new List<MonitorBounds>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate (IntPtr mon, IntPtr hdc, ref RECT r, IntPtr data)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref mi))
            {
                bool primary = (mi.dwFlags & 1) != 0; // MONITORINFOF_PRIMARY
                var rc = mi.rcMonitor;
                list.Add(new MonitorBounds(rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top, primary));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
