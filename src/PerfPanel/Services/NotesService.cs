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

    // ---- 番茄钟(时长持久化;运行态仅存内存,重启归零) ----
    public int PomodoroWorkMinutes { get; set; } = 25;
    public int PomodoroBreakMinutes { get; set; } = 5;

    [JsonIgnore] private bool _pomoRunning;
    [JsonIgnore] private bool _pomoOnBreak;
    [JsonIgnore] private DateTime _pomoEndUtc;     // 运行中的截止时刻
    [JsonIgnore] private TimeSpan _pomoRemaining;  // 暂停时冻结的剩余时长
    [JsonIgnore] private int _pomoCompleted;       // 本次会话完成的专注段数

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
                PomodoroWorkMinutes = Math.Clamp(loaded.PomodoroWorkMinutes, 5, 120);
                PomodoroBreakMinutes = Math.Clamp(loaded.PomodoroBreakMinutes, 1, 60);
                Messages = loaded.Messages is { Count: > 0 } ? loaded.Messages : [.. DefaultMessages];
                LastReminderUtc = loaded.LastReminderUtc;
                _pomoRemaining = TimeSpan.FromMinutes(PomodoroWorkMinutes); // 重启后停在「专注待开始」
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

    // ==================== 番茄钟 ====================

    [JsonIgnore] public bool PomoRunning => _pomoRunning;
    [JsonIgnore] public bool PomoOnBreak => _pomoOnBreak;
    [JsonIgnore] public int PomoCompleted => _pomoCompleted;

    /// <summary>当前段剩余时长:运行中按截止时刻算,暂停取冻结值;从未启动时退回专注全长。</summary>
    public TimeSpan PomoRemainingNow(DateTime utcNow)
    {
        if (_pomoRunning)
        {
            var r = _pomoEndUtc - utcNow;
            return r > TimeSpan.Zero ? r : TimeSpan.Zero;
        }
        return _pomoRemaining > TimeSpan.Zero
            ? _pomoRemaining
            : TimeSpan.FromMinutes(PomodoroWorkMinutes);
    }

    /// <summary>当前段总时长(用于进度环)。</summary>
    [JsonIgnore]
    public double PomoPhaseTotalSeconds => (_pomoOnBreak ? PomodoroBreakMinutes : PomodoroWorkMinutes) * 60.0;

    /// <summary>开始/继续当前段。</summary>
    public void PomoStart(DateTime utcNow)
    {
        var remain = PomoRemainingNow(utcNow);
        if (remain <= TimeSpan.Zero) remain = TimeSpan.FromMinutes(PomoPhaseTotalSeconds / 60);
        _pomoEndUtc = utcNow + remain;
        _pomoRunning = true;
    }

    /// <summary>暂停并冻结剩余时长。</summary>
    public void PomoPause(DateTime utcNow)
    {
        if (!_pomoRunning) return;
        _pomoRemaining = PomoRemainingNow(utcNow);
        _pomoRunning = false;
    }

    /// <summary>复位到「专注·待开始」(不清已完成计数)。</summary>
    public void PomoReset()
    {
        _pomoRunning = false;
        _pomoOnBreak = false;
        _pomoRemaining = TimeSpan.FromMinutes(PomodoroWorkMinutes);
    }

    /// <summary>切换到另一段(专注↔休息),暂停待启动;跳过不计番茄数。</summary>
    public void PomoSkip()
    {
        _pomoRunning = false;
        _pomoOnBreak = !_pomoOnBreak;
        _pomoRemaining = TimeSpan.FromMinutes(_pomoOnBreak ? PomodoroBreakMinutes : PomodoroWorkMinutes);
    }

    /// <summary>每秒推进:到点自动切换。返回 "work-done"(专注完成,自动开始休息)、
    /// "break-done"(休息结束,停在专注待开始)或 null。</summary>
    public string? PomoAdvance(DateTime utcNow)
    {
        if (!_pomoRunning || utcNow < _pomoEndUtc) return null;
        if (!_pomoOnBreak)
        {
            _pomoCompleted++;
            _pomoOnBreak = true;
            _pomoEndUtc = utcNow + TimeSpan.FromMinutes(PomodoroBreakMinutes); // 休息自动接续
            _pomoRemaining = TimeSpan.FromMinutes(PomodoroBreakMinutes);
            return "work-done";
        }
        _pomoOnBreak = false;
        _pomoRunning = false;
        _pomoRemaining = TimeSpan.FromMinutes(PomodoroWorkMinutes);
        return "break-done";
    }

    /// <summary>时长设置变更后同步未开始段的剩余时长(运行中不受影响,下轮生效)。</summary>
    public void PomoSyncDurations()
    {
        if (_pomoRunning) return;
        _pomoRemaining = TimeSpan.FromMinutes(_pomoOnBreak ? PomodoroBreakMinutes : PomodoroWorkMinutes);
    }
}
