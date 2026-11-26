using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Localization;
using Octopus.Core.Bots;
using TKCode.Arc.Localization;

namespace Octopus.App.Bots;

/// <summary>One bot-made or owner-made schedule in the bot right panel's Home tab.</summary>
public sealed partial class BotScheduleRowViewModel : ObservableObject
{
    private readonly string _botId;
    private readonly BotScheduler _scheduler;
    private readonly Action<string>? _openChat;
    private readonly bool _ready;

    public BotScheduleRowViewModel(BotDefinition bot, BotSchedule schedule, DateTimeOffset now, BotScheduler scheduler, Action<string>? openChat)
    {
        _botId = bot.Id;
        _scheduler = scheduler;
        _openChat = openChat;
        Id = schedule.Id;
        IsRecurring = schedule.IsRecurring;
        Title = !string.IsNullOrWhiteSpace(schedule.Title) ? schedule.Title! : FirstLine(schedule.Prompt);
        ScheduleText = BotScheduleFormatter.Describe(schedule, now);
        TargetChatId = BotScheduler.ResolveTargetChatId(bot, schedule);

        (StatusText, IsFailed) = schedule.LastRunStatus switch
        {
            BotScheduleRunStatus.Failed => (AppLangResources.Format(AppLangResourceKind.BotScheduleLastFailed, schedule.LastRunError ?? "?"), true),
            BotScheduleRunStatus.Missed => (AppLangResources.Get(AppLangResourceKind.BotScheduleLastMissed), false),
            _ => ("", false),
        };
        HasStatus = StatusText.Length > 0;

        var tip = $"{schedule.Prompt}\n\n{schedule.Cron ?? schedule.RunAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm")} · {schedule.Id}";
        var chatTitle = bot.Chats.FirstOrDefault(c => c.Id == TargetChatId)?.DisplayTitle;
        if (!string.IsNullOrWhiteSpace(chatTitle))
        {
            tip += "\n" + AppLangResources.Format(AppLangResourceKind.BotScheduleFromChat, chatTitle);
        }
        Tooltip = tip;

        IsEnabled = schedule.Enabled;
        _ready = true;
    }

    public string Id { get; }
    public bool IsRecurring { get; }
    public string Title { get; }
    public string ScheduleText { get; }
    public string StatusText { get; }
    public bool HasStatus { get; }
    public bool IsFailed { get; }
    public string Tooltip { get; }
    public string? TargetChatId { get; }

    [ObservableProperty] public partial bool IsEnabled { get; set; }

    partial void OnIsEnabledChanged(bool value)
    {
        if (_ready) _scheduler.SetEnabled(_botId, Id, value);
    }

    [RelayCommand]
    private void OpenChat()
    {
        if (TargetChatId is not null) _openChat?.Invoke(TargetChatId);
    }

    [RelayCommand]
    private Task RunNowAsync() => _scheduler.RunNowAsync(_botId, Id);

    [RelayCommand]
    private void Delete() => _scheduler.Delete(_botId, Id);

    private static string FirstLine(string prompt)
    {
        var line = prompt.Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "";
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}

/// <summary>Turns a schedule into a short localized line such as "每天 09:03 · 下次 明天 09:03".</summary>
public static class BotScheduleFormatter
{
    private static readonly string[] ZhDays = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];
    private static readonly string[] EnDays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly string[] DayTokens = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private static bool IsZh => ArcLanguage.Current.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    public static string Describe(BotSchedule schedule, DateTimeOffset now)
    {
        if (!schedule.IsRecurring)
        {
            var when = schedule.RunAt is { } at ? FormatWhen(at, now) : "?";
            return AppLangResources.Format(AppLangResourceKind.BotScheduleOnce, when);
        }

        var expr = CronExpression.TryParse(schedule.Cron, out var parsed) ? parsed : null;
        var text = DescribeCron(expr) ?? schedule.Cron!;
        if (!schedule.Enabled)
        {
            return $"{text} · {AppLangResources.Get(AppLangResourceKind.BotScheduleOff)}";
        }
        return schedule.NextRunAt is { } next
            ? $"{text} · {AppLangResources.Format(AppLangResourceKind.BotScheduleNext, FormatWhen(next, now))}"
            : text;
    }

    /// <summary>Localized wording for the common cron shapes; null for anything else.</summary>
    public static string? DescribeCron(CronExpression? expression)
    {
        if (expression is null) return null;
        var f = expression.Fields;
        string m = f[0], h = f[1], dom = f[2], mon = f[3], dow = f[4];
        if (mon != "*") return null;

        if (dom == "*" && dow == "*")
        {
            if (m == "*" && h == "*") return AppLangResources.Format(AppLangResourceKind.BotScheduleEveryMinutes, 1);
            if (m.StartsWith("*/", StringComparison.Ordinal) && h == "*" && int.TryParse(m[2..], out var everyMin))
            {
                return AppLangResources.Format(AppLangResourceKind.BotScheduleEveryMinutes, everyMin);
            }
            if (!int.TryParse(m, out var minute)) return null;
            if (h == "*") return AppLangResources.Format(AppLangResourceKind.BotScheduleHourly, minute.ToString("D2"));
            if (h.StartsWith("*/", StringComparison.Ordinal) && int.TryParse(h[2..], out var everyHour))
            {
                return AppLangResources.Format(AppLangResourceKind.BotScheduleEveryHours, everyHour, minute.ToString("D2"));
            }
            return int.TryParse(h, out var hour)
                ? AppLangResources.Format(AppLangResourceKind.BotScheduleDaily, $"{hour:D2}:{minute:D2}")
                : null;
        }

        if (!int.TryParse(m, out var min) || !int.TryParse(h, out var hr)) return null;
        var time = $"{hr:D2}:{min:D2}";
        if (dom == "*")
        {
            if (dow is "1-5" || string.Equals(dow, "MON-FRI", StringComparison.OrdinalIgnoreCase))
            {
                return AppLangResources.Format(AppLangResourceKind.BotScheduleWeekdays, time);
            }
            if (ParseDays(dow) is not { Count: > 0 } days) return null;
            var names = IsZh ? ZhDays : EnDays;
            var joined = string.Join(IsZh ? "、" : ", ", days.Select(d => names[d]));
            return AppLangResources.Format(AppLangResourceKind.BotScheduleWeekly, joined, time);
        }
        if (dow == "*" && int.TryParse(dom, out var day))
        {
            return AppLangResources.Format(AppLangResourceKind.BotScheduleMonthly, day, time);
        }
        return null;
    }

    public static string FormatWhen(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("HH:mm");
        if (local.Date == today) return time;
        if (local.Date == today.AddDays(1)) return AppLangResources.Format(AppLangResourceKind.BotScheduleTomorrow, time);
        return $"{local.Month}/{local.Day} {time}";
    }

    private static List<int>? ParseDays(string field)
    {
        var days = new SortedSet<int>();
        foreach (var part in field.Split(','))
        {
            var dash = part.IndexOf('-');
            if (dash > 0)
            {
                if (!TryDay(part[..dash], out var a) || !TryDay(part[(dash + 1)..], out var b) || a > b) return null;
                for (var d = a; d <= b; d++) days.Add(d % 7);
            }
            else
            {
                if (!TryDay(part, out var d)) return null;
                days.Add(d % 7);
            }
        }
        return [.. days];
    }

    private static bool TryDay(string text, out int day)
    {
        if (int.TryParse(text, out day)) return day is >= 0 and <= 7;
        day = Array.FindIndex(DayTokens, t => string.Equals(t, text, StringComparison.OrdinalIgnoreCase));
        return day >= 0;
    }
}
