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
        try
        {
            dynamic svc = ConnectService();
            svc.GetFolder("\\").GetTask(TaskName); // 不存在时抛 COM 异常
            return true;
        }
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
            try
            {
                RegisterTask();
                var (runOk, _) = TryRemoveRunKey(); // 避免双重启动
                return (true, $"✓ 已注册开机自启(计划任务 · 最高权限,完整传感器)\n\n任务名:{TaskName}\n程序:{ExePath}" +
                              (runOk ? "\n已顺带清理旧的注册表启动项" : ""));
            }
            catch (Exception ex)
            {
                var fb = TryWriteRunKey();
                return fb.Ok
                    ? (true, $"计划任务注册失败,已改用注册表方式:\n{ex.Message}\n\n建议右键管理员运行后重试。")
                    : (false, $"注册失败:\n{ex.Message}\n{fb.Msg}");
            }
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
            try
            {
                dynamic svc = ConnectService();
                svc.GetFolder("\\").DeleteTask(TaskName, 0);
                lines.Add("✓ 已删除计划任务");
            }
            catch (Exception ex)
            {
                lines.Add($"✗ 计划任务删除失败:{ex.Message}");
            }
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

    private static dynamic ConnectService()
    {
        // Schedule.Service 是 Windows 内置 Task Scheduler 的 COM 接口;
        // 相比拉起 schtasks.exe,无进程启动、无命令行拼接,动态值只进对象属性
        var type = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Task Scheduler COM 不可用");
        dynamic svc = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException("Task Scheduler COM 实例化失败");
        svc.Connect();
        return svc;
    }

    private static void RegisterTask()
    {
        // TASK_CREATE_OR_UPDATE=6,TASK_LOGON_INTERACTIVE_TOKEN=3,
        // TASK_TRIGGER_LOGON=9,TASK_ACTION_EXEC=0,TASK_RUNLEVEL_HIGHEST=1
        dynamic svc = ConnectService();
        dynamic def = svc.NewTask(0);
        def.RegistrationInfo.Description = "PerfPanel 开机自启(完整传感器)";
        def.Principal.RunLevel = 1;
        def.Triggers.Create(9);
        dynamic action = def.Actions.Create(0);
        action.Path = ExePath;
        svc.GetFolder("\\").RegisterTaskDefinition(TaskName, def, 6, null, null, 3);
    }

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
}
