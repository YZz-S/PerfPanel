using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace PerfPanel.Services;

/// <summary>Windows 系统通知(Toast,横幅 + 进通知中心)。
/// 面板是免打包(exe 单文件)应用:AUMID 走 HKCU 注册(免管理员、免快捷方式),
/// 用该 AUMID 创建 Notifier 直接投递;每类通知固定 Tag,同 Tag 旧 toast 被替换,
/// 通知中心不会堆积。任何失败降级为系统提示音,绝不影响面板本体。</summary>
public static class ToastService
{
    private const string Aumid = "PerfPanel.Focus";
    private static bool _registered;

    /// <summary>弹一条通知。tag 区分来源("reminder" 提醒语 / "pomodoro" 番茄钟),
    /// 同类新通知替换同类旧通知。</summary>
    public static void Show(string tag, string title, string body)
    {
        try
        {
            RegisterAumid();
            string xml =
                "<toast>" +
                "<visual><binding template=\"ToastGeneric\">" +
                $"<text>{Escape(title)}</text><text>{Escape(body)}</text>" +
                "</binding></visual>" +
                "<audio src=\"ms-winsoundevent:Notification.Default\"/>" +
                "</toast>";
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            ToastNotificationManager.CreateToastNotifier(Aumid).Show(new ToastNotification(doc)
            {
                Tag = tag,
                Group = "PerfPanel",
            });
        }
        catch
        {
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
        }
    }

    /// <summary>HKCU 注册 AUMID(幂等):DisplayName 与图标(取当前 exe 图标)。
    /// HKCU\Software\Classes\AppUserModelId\{AUMID} 是免打包应用的标准做法,无需提权。</summary>
    private static void RegisterAumid()
    {
        if (_registered) return;
        using var key = Microsoft.Win32.Registry.CurrentUser
            .CreateSubKey($@"Software\Classes\AppUserModelId\{Aumid}");
        if (key != null)
        {
            key.SetValue("DisplayName", "PerfPanel 专注提醒");
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe)) key.SetValue("IconUri", exe);
        }
        _registered = true;
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
