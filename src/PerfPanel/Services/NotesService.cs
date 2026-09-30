using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PerfPanel.Services;

/// <summary>单条待办事项。</summary>
public class TodoItem
{
    public string Text { get; set; } = "";
    public bool Done { get; set; }
}

/// <summary>
/// 便签/待办/专注提醒状态:持久化到 exe 旁 notes.json。
/// 面板卡片展示便签与待办,并按 ReminderMinutes 周期轮换提醒语(专注复习、查看复习计划看板等)。
/// </summary>
public sealed class NotesService
{
    public string Note { get; set; } = "";
    public List<TodoItem> Todos { get; set; } = [];
    public int ReminderMinutes { get; set; } = 30;
    public List<string> Messages { get; set; } = DefaultMessages.ToList();
    public DateTime LastReminderUtc { get; set; } = DateTime.UtcNow; // 启动后先安静一个周期

    [JsonIgnore]
    private int _rotateIndex;

    [JsonIgnore]
    private readonly object _lock = new();

    public static readonly string[] DefaultMessages =
    [
        "专注复习,别被别的事吸引注意力",
        "记得查看复习计划看板",
        "复习走神了?回到当前任务上",
        "休息也要有度,喝水远眺 20 秒",
        "今天的目标完成了吗?",
    ];

    private static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "notes.json");

    public static NotesService Instance { get; } = new();

    public void Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(Path)) return;
                var loaded = JsonSerializer.Deserialize<NotesService>(File.ReadAllText(Path));
                if (loaded == null) return;
                Note = loaded.Note;
                Todos = loaded.Todos ?? [];
                ReminderMinutes = Math.Clamp(loaded.ReminderMinutes, 5, 240);
                Messages = loaded.Messages is { Count: > 0 } ? loaded.Messages : [.. DefaultMessages];
                LastReminderUtc = loaded.LastReminderUtc;
            }
            catch { /* 损坏文件按默认值用 */ }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    /// <summary>未完成待办(保持原顺序)。</summary>
    [JsonIgnore]
    public IEnumerable<TodoItem> PendingTodos => Todos.Where(t => !t.Done);

    /// <summary>提醒周期是否已到。</summary>
    public bool IsReminderDue(DateTime utcNow) => Messages.Count > 0 && utcNow - LastReminderUtc >= TimeSpan.FromMinutes(ReminderMinutes);

    /// <summary>本轮提醒语,并推进轮换、重置周期计时。</summary>
    public string TakeReminder(DateTime utcNow)
    {
        lock (_lock)
        {
            LastReminderUtc = utcNow;
            var msg = Messages[_rotateIndex % Messages.Count];
            _rotateIndex++;
            Save();
            return msg;
        }
    }

    /// <summary>下一次将轮到的提醒语预览(不推进)。</summary>
    [JsonIgnore]
    public string NextMessagePreview
    {
        get
        {
            lock (_lock)
            {
                return Messages.Count == 0 ? "" : Messages[_rotateIndex % Messages.Count];
            }
        }
    }

    public void ToggleTodo(TodoItem item)
    {
        item.Done = !item.Done;
        Save();
    }

    public void AddTodo(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        Todos.Add(new TodoItem { Text = text });
        Save();
    }

    public void RemoveTodo(TodoItem item)
    {
        Todos.Remove(item);
        Save();
    }
}
