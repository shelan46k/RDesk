using Microsoft.UI.Xaml;
using RDesk.Models;

namespace RDesk.Services;

/// <summary>
/// Fires todo reminders (Windows tray notification + in-panel banner with snooze), and rolls
/// repeating todos forward when they are completed.
/// </summary>
public sealed class ReminderService
{
    private readonly AppHost _host;
    private DispatcherTimer? _timer;

    public event EventHandler<TodoItemModel>? ReminderDue;

    public ReminderService(AppHost host)
    {
        _host = host;
    }

    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _timer.Tick += (_, _) => CheckNow(DateTime.Now);
        _timer.Start();
        CheckNow(DateTime.Now);
    }

    /// <returns>Todos that fired during this check.</returns>
    public List<TodoItemModel> CheckNow(DateTime now)
    {
        var fired = new List<TodoItemModel>();
        foreach (var todo in _host.Config.Current.TodoItems.ToList())
        {
            if (todo.Reminded || todo.NextReminderAt is not { } at || at > now)
            {
                continue;
            }

            todo.Reminded = true;
            todo.SnoozeUntil = null;
            fired.Add(todo);
        }

        if (fired.Count == 0)
        {
            return fired;
        }

        _host.Config.SaveDebounced();
        foreach (var todo in fired)
        {
            var due = todo.DueAt is { } d ? $"截止 {FormatDue(d)}" : string.Empty;
            _host.Tray?.ShowNotification($"⏰ 待辦提醒：{todo.Content}", string.IsNullOrEmpty(due) ? "點系統匣圖示開啟 RDesk" : due);
            ReminderDue?.Invoke(this, todo);
        }

        return fired;
    }

    public static void Snooze(TodoItemModel todo, TimeSpan delay, DateTime now) => SnoozeUntil(todo, now + delay);

    public static void SnoozeUntil(TodoItemModel todo, DateTime when)
    {
        todo.SnoozeUntil = when;
        todo.Reminded = false;
        todo.RemindEnabled = true;
        todo.DueAt ??= when;
    }

    /// <summary>Creates the next occurrence of a repeating todo, or null.</summary>
    public static TodoItemModel? NextOccurrence(TodoItemModel done)
    {
        if (done.Repeat is "None" or "" || string.IsNullOrEmpty(done.Repeat))
        {
            return null;
        }

        var baseDate = done.DueAt ?? DateTime.Today.AddHours(9);
        var next = done.Repeat switch
        {
            "Daily" => baseDate.AddDays(1),
            "Weekdays" => NextWeekday(baseDate),
            "Weekly" => baseDate.AddDays(7),
            "Monthly" => baseDate.AddMonths(1),
            _ => baseDate.AddDays(1)
        };
        // Catch up if the task was finished late, so the next one is in the future.
        while (next < DateTime.Now.Date)
        {
            next = done.Repeat switch
            {
                "Weekly" => next.AddDays(7),
                "Monthly" => next.AddMonths(1),
                "Weekdays" => NextWeekday(next),
                _ => next.AddDays(1)
            };
        }

        return new TodoItemModel
        {
            Content = done.Content,
            IsImportant = done.IsImportant,
            Color = done.Color,
            DueAt = next,
            RemindEnabled = done.RemindEnabled,
            RemindLeadMinutes = done.RemindLeadMinutes,
            Repeat = done.Repeat,
            Attachments = done.Attachments.ToList()
        };
    }

    private static DateTime NextWeekday(DateTime from)
    {
        var next = from.AddDays(1);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            next = next.AddDays(1);
        }

        return next;
    }

    public static string FormatDue(DateTime due)
    {
        var today = DateTime.Today;
        var day = due.Date == today ? "今天"
            : due.Date == today.AddDays(1) ? "明天"
            : due.Date == today.AddDays(-1) ? "昨天"
            : due.Year == today.Year ? due.ToString("M/d") : due.ToString("yyyy/M/d");
        return due.TimeOfDay == TimeSpan.Zero ? day : $"{day} {due:HH:mm}";
    }

    public static string RepeatLabel(string repeat) => repeat switch
    {
        "Daily" => "每天",
        "Weekdays" => "工作日",
        "Weekly" => "每週",
        "Monthly" => "每月",
        _ => string.Empty
    };
}
