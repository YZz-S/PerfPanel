using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;

namespace PerfPanel.Services;

/// <summary>开机自启管理:管理员注册计划任务(完整传感器),非管理员退回注册表 Run 键。</summary>
public static class AutostartService
{
    private const string TaskName = "PerfPanelAutostart";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PerfPanel";

    public static string ExePath => Environment.ProcessPath ?? "";

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool IsTaskRegistered()
    {
        try { return RunSchtasks($"/Query /TN {TaskName}").ExitCode == 0; }
        catch { return false; }
    }

    public static bool IsRunKeyRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string s && s.Contains("PerfPanel", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static (bool Ok, string Msg) Enable()
    {
        if (IsElevated())
        {
            var p = RunSchtasks($"/Create /F /TN {TaskName} /TR \"\\\"{ExePath}\\\"\" /SC ONLOGON /RL HIGHEST");
            if (p.ExitCode == 0)
            {
                var (runOk, _) = TryRemoveRunKey(); // 避免双重启动
                return (true, $"✓ 已注册开机自启(计划任务 · 最高权限,完整传感器)\n\n任务名:{TaskName}\n程序:{ExePath}" +
                              (runOk ? "\n已顺带清理旧的注册表启动项" : ""));
            }
            var fb = TryWriteRunKey();
            return fb.Ok
                ? (true, $"计划任务注册失败,已改用注册表方式:\n{p.Output}\n\n建议右键管理员运行后重试。")
                : (false, $"注册失败:\n{p.Output}\n{fb.Msg}");
        }

        var rk = TryWriteRunKey();
        return rk.Ok
            ? (true, "✓ 已注册开机自启(当前用户 · 注册表方式)\n\n" +
                     "注意:开机后以普通权限运行,CPU 温度/功耗/风扇不可用。\n" +
                     "想要完整传感器:右键\"以管理员身份运行\"本程序,再执行一次 --autostart-on。")
            : (false, $"注册失败:{rk.Msg}");
    }

    public static (bool Ok, string Msg) Disable()
    {
        var lines = new List<string>();
        if (IsTaskRegistered())
        {
            var p = RunSchtasks($"/Delete /F /TN {TaskName}");
            lines.Add(p.ExitCode == 0 ? "✓ 已删除计划任务" : $"✗ 计划任务删除失败:{p.Output}");
        }
        var (ok, msg) = TryRemoveRunKey();
        if (ok) lines.Add("✓ 已清理注册表启动项");
        else if (!string.IsNullOrEmpty(msg)) lines.Add($"✗ {msg}");
        if (lines.Count == 0) lines.Add("(未发现任何自启注册)");
        return (true, string.Join("\n", lines));
    }

    public static string Status() => IsElevated()
        ? $"当前进程:管理员\n计划任务:{(IsTaskRegistered() ? "已注册" : "未注册")}\n注册表:{(IsRunKeyRegistered() ? "已注册" : "未注册")}"
        : $"当前进程:普通权限\n计划任务:{(IsTaskRegistered() ? "已注册" : "未注册")}\n注册表:{(IsRunKeyRegistered() ? "已注册" : "未注册")}";

    private static (bool Ok, string Msg) TryWriteRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            key.SetValue(RunValue, $"\"{ExePath}\"");
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (bool Ok, string Msg) TryRemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(RunValue) == null) return (false, "");
            key.DeleteValue(RunValue);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (int ExitCode, string Output) RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 schtasks");
        string output = (p.StandardOutput.ReadToEnd() + " " + p.StandardError.ReadToEnd()).Trim();
        p.WaitForExit(10_000);
        return (p.ExitCode, output);
    }
}
