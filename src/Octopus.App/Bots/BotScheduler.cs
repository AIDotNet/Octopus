using Avalonia.Threading;
using Octopus.App.Collab;
using Octopus.App.Workspace;
using Octopus.Core.Bots;

namespace Octopus.App.Bots;

/// <summary>
/// Fires bots' schedules: at each due time the schedule's prompt is sent into a chat of the bot, whatever agent runs
/// it. Schedules are saved with the bot, so they survive closing the chat and restarting Octopus; they only fire while
/// Octopus runs. Also turns the look-around (巡视) and memory-maintenance (记忆维护) switches into real schedules.
/// All methods run on the UI thread.
/// </summary>
public sealed class BotScheduler : IDisposable
{
    /// <summary>A bot's own schedule that was due longer ago than this (Octopus was not running) is skipped, not run late.</summary>
    public static readonly TimeSpan MaxLateness = TimeSpan.FromHours(12);
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);
    public const int MaxSchedulesPerBot = 50;
    public const string MemoryMaintenanceCron = "0 3 * * *";

    internal const string LookAroundPrompt =
        "Look around: check your connected apps, open work items and recent chats for anything new that needs the owner's attention. " +
        "Report only what needs action, briefly. If nothing needs attention, reply with one short line saying so. " +
        "Record durable findings in MEMORY.md.";

    internal const string MemoryMaintenancePrompt =
        "Memory maintenance: tidy up MEMORY.md. Merge duplicates, remove entries that are stale or wrong, keep one durable fact " +
        "or preference per line, and move long notes into memory/. Do not invent facts. Reply with a one-line summary of what changed.";

    private readonly IBotService _bots;
    private readonly Func<ITabContentRegistry?> _tabs;
    private readonly TimeProvider _time;
    private DispatcherTimer? _timer;
    private bool _ticking;
    private bool _syncing;

    public BotScheduler(IBotService bots, Func<ITabContentRegistry?> tabs, TimeProvider? time = null)
    {
        _bots = bots;
        _tabs = tabs;
        _time = time ?? TimeProvider.System;
        _bots.Changed += OnBotsChanged;
    }

    /// <summary>Replaces delivery in tests: (bot, chat id, prompt text) → delivered.</summary>
    internal Func<BotDefinition, string, string, Task<bool>>? DeliverOverride { get; set; }

    public void Start()
    {
        foreach (var bot in _bots.Bots.ToList())
        {
            SyncBuiltIns(bot.Id);
        }
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = TickInterval };
            _timer.Tick += (_, _) => _ = TickAsync();
            _timer.Start();
        }
        _ = TickAsync();
    }

    /// <summary>Fires every schedule that is due. Safe to call at any time; overlapping calls do nothing.</summary>
    public async Task TickAsync()
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            var now = _time.GetUtcNow();
            foreach (var bot in _bots.Bots.ToList())
            {
                var due = new List<BotSchedule>();
                _bots.MutateSchedules(bot.Id, list =>
                {
                    var changed = false;
                    foreach (var s in list.ToList())
                    {
                        if (!s.Enabled) continue;
                        if (s.NextRunAt is null)
                        {
                            s.NextRunAt = ComputeNext(s, now);
                            if (s.NextRunAt is null)
                            {
                                // A cron that never matches, or a one-shot without a time: nothing to wait for.
                                s.Enabled = false;
                            }
                            changed = true;
                            continue;
                        }
                        if (s.NextRunAt > now || bot.IsPaused) continue;

                        var late = now - s.NextRunAt.Value;
                        s.LastRunAt = now;
                        if (!s.IsBuiltIn && late > MaxLateness)
                        {
                            s.LastRunStatus = BotScheduleRunStatus.Missed;
                            s.LastRunError = null;
                        }
                        else
                        {
                            s.LastRunStatus = BotScheduleRunStatus.Sent;
                            s.LastRunError = null;
                            s.RunCount++;
                            due.Add(s);
                        }

                        if (s.IsRecurring)
                        {
                            // Missed occurrences collapse into this one run.
                            s.NextRunAt = ComputeNext(s, now);
                        }
                        else
                        {
                            s.NextRunAt = null;
                            s.Enabled = false;
                            if (s.LastRunStatus == BotScheduleRunStatus.Sent)
                            {
                                list.Remove(s);
                            }
                        }
                        changed = true;
                    }
                    return changed;
                });

                foreach (var schedule in due)
                {
                    var (ok, error) = await DeliverAsync(bot.Id, schedule).ConfigureAwait(true);
                    if (!ok)
                    {
                        RecordFailure(bot.Id, schedule, error);
                    }
                }
            }
        }
        finally
        {
            _ticking = false;
        }
    }

    /// <summary>Adds a bot-made or owner-made schedule. Throws <see cref="ArgumentException"/> with a readable reason.</summary>
    public BotSchedule Create(string botId, string prompt, string? cron, DateTimeOffset? runAt, string? title = null, string? chatId = null, string createdBy = "agent")
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("prompt is required.");
        var hasCron = !string.IsNullOrWhiteSpace(cron);
        if (hasCron == runAt.HasValue) throw new ArgumentException("Give exactly one of cron (recurring) or run_at (one-shot).");

        var now = _time.GetUtcNow();
        if (hasCron && !CronExpression.TryParse(cron, out _))
        {
            throw new ArgumentException($"'{cron}' is not a valid 5-field cron expression (minute hour day-of-month month day-of-week).");
        }
        if (runAt is { } at && at <= now)
        {
            throw new ArgumentException("run_at is in the past.");
        }

        var schedule = new BotSchedule
        {
            Kind = BotScheduleKinds.Custom,
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
            Prompt = prompt.Trim(),
            Cron = hasCron ? cron!.Trim() : null,
            RunAt = runAt,
            ChatId = string.IsNullOrWhiteSpace(chatId) ? null : chatId,
            CreatedBy = createdBy,
            CreatedAt = now,
        };
        schedule.NextRunAt = ComputeNext(schedule, now) ?? throw new ArgumentException($"'{cron}' never matches a date.");

        var full = false;
        var added = _bots.MutateSchedules(botId, list =>
        {
            if (list.Count(s => !s.IsBuiltIn) >= MaxSchedulesPerBot)
            {
                full = true;
                return false;
            }
            while (list.Any(s => s.Id == schedule.Id))
            {
                schedule.Id = Guid.NewGuid().ToString("N")[..8];
            }
            list.Add(schedule);
            return true;
        });
        if (full) throw new ArgumentException($"This bot already has {MaxSchedulesPerBot} schedules. Delete some first.");
        if (!added) throw new ArgumentException("Unknown bot.");
        return schedule;
    }

    /// <summary>Removes a bot-made or owner-made schedule. Built-in automations are switched off instead.</summary>
    public bool Delete(string botId, string scheduleId) =>
        _bots.MutateSchedules(botId, list => list.RemoveAll(s => s.Id == scheduleId && !s.IsBuiltIn) > 0);

    public void SetEnabled(string botId, string scheduleId, bool enabled)
    {
        var bot = _bots.Bots.FirstOrDefault(b => b.Id == botId);
        var schedule = bot?.Schedules.FirstOrDefault(s => s.Id == scheduleId);
        if (bot is null || schedule is null) return;

        switch (schedule.Kind)
        {
            case BotScheduleKinds.LookAround:
                _bots.UpdateBotSettings(botId, b => b.LookAroundEnabled = enabled);
                return;
            case BotScheduleKinds.MemoryMaintenance:
                _bots.UpdateBotSettings(botId, b => b.TidyUpMemoryEnabled = enabled);
                return;
        }

        var now = _time.GetUtcNow();
        _bots.MutateSchedules(botId, list =>
        {
            if (list.FirstOrDefault(s => s.Id == scheduleId) is not { } s || s.Enabled == enabled) return false;
            s.Enabled = enabled;
            // Re-enabling starts from now: runs missed while it was off are not made up.
            s.NextRunAt = enabled ? ComputeNext(s, now) : null;
            if (enabled && s.NextRunAt is null) s.Enabled = false;
            return true;
        });
    }

    /// <summary>Sends the schedule's prompt right away, without moving its next run.</summary>
    public async Task<bool> RunNowAsync(string botId, string scheduleId)
    {
        var schedule = _bots.Bots.FirstOrDefault(b => b.Id == botId)?.Schedules.FirstOrDefault(s => s.Id == scheduleId);
        if (schedule is null) return false;
        var now = _time.GetUtcNow();
        _bots.MutateSchedules(botId, list =>
        {
            if (list.FirstOrDefault(s => s.Id == scheduleId) is not { } s) return false;
            s.LastRunAt = now;
            s.LastRunStatus = BotScheduleRunStatus.Sent;
            s.LastRunError = null;
            s.RunCount++;
            return true;
        });
        var (ok, error) = await DeliverAsync(botId, schedule).ConfigureAwait(true);
        if (!ok) RecordFailure(botId, schedule, error);
        return ok;
    }

    /// <summary>The next due time strictly after <paramref name="now"/>; null when there is none.</summary>
    public static DateTimeOffset? ComputeNext(BotSchedule schedule, DateTimeOffset now)
    {
        if (schedule.IsRecurring)
        {
            return CronExpression.TryParse(schedule.Cron, out var expr) ? expr!.Next(now) : null;
        }
        return schedule.RunAt is { } at && at > now ? at : null;
    }

    /// <summary>The chat that receives a schedule's runs: its own chat while it exists, else the Home chat.</summary>
    public static string? ResolveTargetChatId(BotDefinition bot, BotSchedule schedule)
    {
        if (!string.IsNullOrWhiteSpace(schedule.ChatId) && bot.Chats.Any(c => c.Id == schedule.ChatId))
        {
            return schedule.ChatId;
        }
        if (!string.IsNullOrWhiteSpace(bot.HomeChatId) && bot.Chats.Any(c => c.Id == bot.HomeChatId))
        {
            return bot.HomeChatId;
        }
        return bot.Chats.FirstOrDefault()?.Id;
    }

    internal static string BuildEnvelope(BotSchedule schedule)
    {
        var label = schedule.Kind switch
        {
            BotScheduleKinds.LookAround => "look-around",
            BotScheduleKinds.MemoryMaintenance => "memory maintenance",
            _ => string.IsNullOrWhiteSpace(schedule.Title) ? $"schedule {schedule.Id}" : $"schedule {schedule.Id} “{schedule.Title}”",
        };
        return $"[Octopus scheduled run: {label}]\n{schedule.Prompt}";
    }

    private async Task<(bool Ok, string? Error)> DeliverAsync(string botId, BotSchedule schedule)
    {
        var bot = _bots.Bots.FirstOrDefault(b => b.Id == botId);
        if (bot is null) return (false, "The bot no longer exists.");
        var chatId = ResolveTargetChatId(bot, schedule);
        if (chatId is null) return (false, "The bot has no chat to run in.");
        var text = BuildEnvelope(schedule);

        try
        {
            if (DeliverOverride is { } over)
            {
                return await over(bot, chatId, text).ConfigureAwait(true) ? (true, null) : (false, "Delivery failed.");
            }

            var item = _bots.EnsureChatOpenInBackground(botId, chatId);
            var updated = _bots.Bots.FirstOrDefault(b => b.Id == botId);
            if (item is null || updated is null) return (false, "The chat no longer exists.");
            var project = _bots.GetBackingProject(updated);
            var control = _tabs()?.GetOrCreate(project, item);
            if (control?.DataContext is not ICollabSessionBridge session)
            {
                return (false, "This chat runs in a terminal; scheduled runs need a chat in the Octopus conversation view.");
            }
            var ok = await session.DeliverCollabPromptAsync(text).ConfigureAwait(true);
            return ok ? (true, null) : (false, "The chat did not accept the prompt.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (false, ex.Message);
        }
    }

    private void RecordFailure(string botId, BotSchedule schedule, string? error)
    {
        _bots.MutateSchedules(botId, list =>
        {
            var target = list.FirstOrDefault(s => s.Id == schedule.Id);
            if (target is null)
            {
                // A one-shot is removed once sent; keep it (switched off) so the failure stays visible.
                target = schedule;
                target.Enabled = false;
                list.Add(target);
            }
            target.LastRunStatus = BotScheduleRunStatus.Failed;
            target.LastRunError = error;
            return true;
        });
    }

    private void OnBotsChanged(object? sender, BotChangedEventArgs e)
    {
        // Settings toggles and new bots arrive as Bots changes; our own writes arrive as Schedules changes.
        if (e.Kind != BotChangeKind.Bots) return;
        if (e.Bot is { } bot)
        {
            SyncBuiltIns(bot.Id);
        }
        else
        {
            foreach (var b in _bots.Bots.ToList()) SyncBuiltIns(b.Id);
        }
    }

    /// <summary>Keeps the built-in schedules in line with the bot's look-around and memory-maintenance settings.</summary>
    internal void SyncBuiltIns(string botId)
    {
        if (_syncing) return;
        var bot = _bots.Bots.FirstOrDefault(b => b.Id == botId);
        if (bot is null) return;
        _syncing = true;
        try
        {
            var now = _time.GetUtcNow();
            _bots.MutateSchedules(botId, list =>
                SyncBuiltIn(list, BotScheduleKinds.LookAround, bot.LookAroundEnabled, LookAroundCron(bot.LookAroundIntervalHours), LookAroundPrompt, now)
                | SyncBuiltIn(list, BotScheduleKinds.MemoryMaintenance, bot.TidyUpMemoryEnabled, MemoryMaintenanceCron, MemoryMaintenancePrompt, now));
        }
        finally
        {
            _syncing = false;
        }
    }

    public static string LookAroundCron(int intervalHours)
    {
        var step = Math.Clamp(intervalHours, 1, 24);
        return step switch
        {
            1 => "0 * * * *",
            24 => "0 0 * * *",
            _ => $"0 */{step} * * *",
        };
    }

    private static bool SyncBuiltIn(List<BotSchedule> list, string kind, bool enabled, string cron, string prompt, DateTimeOffset now)
    {
        var s = list.FirstOrDefault(x => x.Kind == kind);
        if (s is null)
        {
            list.Add(new BotSchedule
            {
                Id = kind,
                Kind = kind,
                Prompt = prompt,
                Cron = cron,
                Enabled = enabled,
                CreatedBy = "octopus",
                CreatedAt = now,
                NextRunAt = enabled && CronExpression.TryParse(cron, out var e) ? e!.Next(now) : null,
            });
            return true;
        }

        var changed = false;
        if (s.Prompt != prompt)
        {
            s.Prompt = prompt;
            changed = true;
        }
        if (s.Cron != cron || s.Enabled != enabled)
        {
            s.Cron = cron;
            s.Enabled = enabled;
            // A new interval, or switching back on, starts from now.
            s.NextRunAt = enabled && CronExpression.TryParse(cron, out var e) ? e!.Next(now) : null;
            changed = true;
        }
        return changed;
    }

    public void Dispose()
    {
        _bots.Changed -= OnBotsChanged;
        _timer?.Stop();
        _timer = null;
    }
}
