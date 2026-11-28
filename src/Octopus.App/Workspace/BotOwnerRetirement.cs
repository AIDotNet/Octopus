using System.Runtime.ExceptionServices;
using Octopus.App.Bots;
using Octopus.Core.Bots;
using Octopus.Terminal.Avalonia;

namespace Octopus.App.Workspace;

/// <summary>
/// Coherent owner-retirement transaction shared across remote host, dock, sidebar, and workspace close/delete routes.
/// Captures the active owner before mutating bot state, always starts (and for async callers awaits) cleanup even if
/// an observer throws during registry removal or bot notification, and never steals a replacement owner.
/// </summary>
internal static class BotOwnerRetirement
{
    [ThreadStatic]
    private static Dictionary<string, int>? s_activeRetirements;

    /// <summary>
    /// True while an initiating close/delete route on this thread is mutating bot state for <paramref name="itemId"/>
    /// after already capturing its original owner. Reactive observers (dock/workspace) must not recapture the key
    /// during this scope, so a replacement attached during removal is preserved if a later observer reopens the item.
    /// </summary>
    public static bool IsRetiring(string itemId) =>
        s_activeRetirements is { } active && active.TryGetValue(itemId, out var count) && count > 0;

    private static void EnterRetirement(IReadOnlyList<string> itemIds)
    {
        var active = s_activeRetirements ??= new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var itemId in itemIds)
        {
            active[itemId] = active.GetValueOrDefault(itemId) + 1;
        }
    }

    private static void ExitRetirement(IReadOnlyList<string> itemIds)
    {
        if (s_activeRetirements is not { } active) return;
        foreach (var itemId in itemIds)
        {
            if (active.TryGetValue(itemId, out var count))
            {
                if (count <= 1) active.Remove(itemId);
                else active[itemId] = count - 1;
            }
        }
    }
    public static IReadOnlyList<string> CollectBotItemIds(IBotService bots, BotDefinition bot)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chat in bot.Chats)
        {
            ids.Add(chat.Id);
        }
        foreach (var item in bots.GetBackingProject(bot).Items)
        {
            ids.Add(item.Id);
        }
        return [.. ids];
    }

    public static bool IsChatDeleted(IBotService bots, string botId, string chatId) =>
        bots.Bots.FirstOrDefault(b => b.Id == botId)?.Chats.All(c => c.Id != chatId) ?? true;

    public static bool IsBotDeleted(IBotService bots, string botId) =>
        bots.Bots.All(b => b.Id != botId);

    public static void RetireChatAndMutate(
        IBotService bots,
        ITabContentRegistry? tabs,
        string botId,
        string chatId,
        bool delete)
    {
        var bot = bots.Bots.FirstOrDefault(b => b.Id == botId);
        var chat = bot?.Chats.FirstOrDefault(c => c.Id == chatId);
        if (bot is null || chat is null || (delete && chat.Kind == BotChatKind.Home))
        {
            return;
        }
        RetireAndMutate(
            tabs,
            chatId,
            () =>
            {
                if (delete) bots.DeleteChat(botId, chatId);
                else bots.CloseChat(botId, chatId);
            },
            isDeleted: delete ? id => IsChatDeleted(bots, botId, id) : null);
    }

    public static void RetireBotAndDelete(IBotService bots, ITabContentRegistry? tabs, string botId)
    {
        var bot = bots.Bots.FirstOrDefault(b => b.Id == botId);
        if (bot is null)
        {
            return;
        }
        RetireAndMutate(
            tabs,
            CollectBotItemIds(bots, bot),
            () => bots.DeleteBot(botId),
            isDeleted: _ => IsBotDeleted(bots, botId));
    }

    public static void RetireAndMutate(
        ITabContentRegistry? tabs,
        string itemId,
        Action mutate,
        Action<Func<Task>>? scheduleRelease = null,
        Func<string, bool>? isDeleted = null) =>
        RetireAndMutate(tabs, [itemId], mutate, scheduleRelease, isDeleted);

    public static void RetireAndMutate(
        ITabContentRegistry? tabs,
        IReadOnlyList<string> itemIds,
        Action mutate,
        Action<Func<Task>>? scheduleRelease = null,
        Func<string, bool>? isDeleted = null)
    {
        // When a caller has no registry (e.g. a sidebar constructed with default tabs=null), do not enter a
        // retirement scope so dock and workspace observers still perform their own capture and cleanup.
        if (tabs is null)
        {
            mutate();
            return;
        }

        var captured = new List<(string ItemId, Func<Task>? Handle)>(itemIds.Count);
        ExceptionDispatchInfo? primaryError = null;
        var enteredMutate = false;
        try
        {
            foreach (var itemId in itemIds)
            {
                captured.Add((itemId, tabs.CaptureForRelease(itemId)));
            }
            enteredMutate = true;
            EnterRetirement(itemIds);
            try
            {
                mutate();
            }
            finally
            {
                ExitRetirement(itemIds);
            }
            if (isDeleted is not null)
            {
                foreach (var itemId in itemIds)
                {
                    if (isDeleted(itemId))
                    {
                        DrainResidualTabs(tabs, itemId, captured, swallowSyncError: false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            primaryError = ExceptionDispatchInfo.Capture(ex);
        }

        if (isDeleted is not null && enteredMutate && primaryError is not null)
        {
            foreach (var itemId in itemIds)
            {
                if (isDeleted(itemId))
                {
                    DrainResidualTabs(tabs, itemId, captured, swallowSyncError: true);
                }
            }
        }

        var swallowSync = primaryError is not null;
        var handledIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (itemId, handle) in captured)
        {
            handledIds.Add(itemId);
            if (handle is not null)
            {
                StartOrSchedule(handle, scheduleRelease, swallowSync);
            }
        }
        // If CaptureForRelease threw during a synchronous removal notification before returning a handle,
        // the detached owner is already registered as a pending capture on tabs; start/schedule it via
        // WaitForReleaseAsync without touching any replacement owner.
        if (primaryError is not null)
        {
            foreach (var itemId in itemIds)
            {
                if (!handledIds.Contains(itemId) || (isDeleted is not null && isDeleted(itemId)))
                {
                    var id = itemId;
                    StartOrSchedule(() => tabs.WaitForReleaseAsync(id), scheduleRelease, swallowSyncError: true);
                }
            }
        }

        primaryError?.Throw();
    }

    public static async Task RetireAndMutateAsync(
        ITabContentRegistry tabs,
        string itemId,
        Action mutate)
    {
        Func<Task>? captured = null;
        ExceptionDispatchInfo? primaryError = null;
        try
        {
            captured = tabs.CaptureForRelease(itemId);
            EnterRetirement([itemId]);
            try
            {
                mutate();
            }
            finally
            {
                ExitRetirement([itemId]);
            }
        }
        catch (Exception ex)
        {
            primaryError = ExceptionDispatchInfo.Capture(ex);
        }

        var cleanupTasks = new List<Task>(2);
        var swallowSync = primaryError is not null;
        if (captured is not null)
        {
            AddCleanupTask(cleanupTasks, captured, swallowSync);
        }
        AddCleanupTask(cleanupTasks, () => tabs.WaitForReleaseAsync(itemId), swallowSync);

        var cleanup = cleanupTasks.Count switch
        {
            0 => Task.CompletedTask,
            1 => cleanupTasks[0],
            _ => Task.WhenAll(cleanupTasks),
        };

        if (primaryError is not null)
        {
            try
            {
                await cleanup.ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original failure without leaking the retiring owner.
            }
            primaryError.Throw();
        }
        await cleanup.ConfigureAwait(false);
    }

    public static async Task RetireMutateAndAwaitAsync(
        Func<Func<(Task Cleanup, ExceptionDispatchInfo? Error)>, Task<(Task Cleanup, ExceptionDispatchInfo? Error)>> runOnUi,
        ITabContentRegistry tabs,
        TerminalSessionRegistry sessions,
        Func<IReadOnlyList<string>> prepareIds,
        Action mutate,
        Func<string, bool>? isDeleted = null)
    {
        var (cleanupTask, error) = await runOnUi(() =>
        {
            var itemIds = prepareIds();
            var originalSessions = new Dictionary<string, TerminalSession>(StringComparer.Ordinal);
            foreach (var itemId in itemIds)
            {
                if (sessions.Find(itemId) is { } existing)
                {
                    originalSessions[itemId] = existing;
                }
            }

            var capturedTabs = new List<Func<Task>>(itemIds.Count);
            var fallbackSessions = new List<TerminalSession>(itemIds.Count);
            ExceptionDispatchInfo? capturedError = null;
            var enteredMutate = false;
            try
            {
                foreach (var itemId in itemIds)
                {
                    var capturedTab = tabs.CaptureForRelease(itemId);
                    if (capturedTab is not null)
                    {
                        capturedTabs.Add(capturedTab);
                    }
                    else if (originalSessions.TryGetValue(itemId, out var original)
                        && sessions.TakeDetached(itemId, only: original) is { } detached)
                    {
                        // Only reached when ITabContentRegistry does not share sessions (e.g. test fakes).
                        // Using the original identity guarantees a replacement session is never taken.
                        fallbackSessions.Add(detached);
                        sessions.NotifyChanged(itemId);
                    }
                }
                enteredMutate = true;
                EnterRetirement(itemIds);
                try
                {
                    mutate();
                }
                finally
                {
                    ExitRetirement(itemIds);
                }
                if (isDeleted is not null)
                {
                    foreach (var itemId in itemIds)
                    {
                        if (isDeleted(itemId))
                        {
                            DrainResidualAsync(tabs, sessions, itemId, capturedTabs, fallbackSessions, swallowSyncError: false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                capturedError = ExceptionDispatchInfo.Capture(ex);
            }

            if (isDeleted is not null && enteredMutate && capturedError is not null)
            {
                foreach (var itemId in itemIds)
                {
                    if (isDeleted(itemId))
                    {
                        DrainResidualAsync(tabs, sessions, itemId, capturedTabs, fallbackSessions, swallowSyncError: true);
                    }
                }
            }

            var cleanupTasks = new List<Task>(capturedTabs.Count + itemIds.Count + fallbackSessions.Count);
            var swallowSync = capturedError is not null;
            foreach (var handle in capturedTabs)
            {
                AddCleanupTask(cleanupTasks, handle, swallowSync);
            }
            foreach (var itemId in itemIds)
            {
                var id = itemId;
                AddCleanupTask(cleanupTasks, () => tabs.WaitForReleaseAsync(id), swallowSync);
            }
            foreach (var session in fallbackSessions)
            {
                var detached = session;
                AddCleanupTask(cleanupTasks, () => detached.DisposeAsync().AsTask(), swallowSync);
            }

            var cleanup = cleanupTasks.Count switch
            {
                0 => Task.CompletedTask,
                1 => cleanupTasks[0],
                _ => Task.WhenAll(cleanupTasks),
            };
            return (cleanup, capturedError);
        }).ConfigureAwait(false);

        if (error is not null)
        {
            try
            {
                await cleanupTask.ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original failure without leaking the retiring owner.
            }
            error.Throw();
        }
        await cleanupTask.ConfigureAwait(false);
    }

    private static void DrainResidualTabs(
        ITabContentRegistry tabs,
        string itemId,
        List<(string ItemId, Func<Task>? Handle)> captured,
        bool swallowSyncError)
    {
        for (var i = 0; i < 8; i++)
        {
            Func<Task>? extra;
            try
            {
                extra = tabs.CaptureForRelease(itemId);
            }
            catch when (swallowSyncError)
            {
                return;
            }
            if (extra is null)
            {
                return;
            }
            captured.Add((itemId, extra));
        }
    }

    private static void DrainResidualAsync(
        ITabContentRegistry tabs,
        TerminalSessionRegistry sessions,
        string itemId,
        List<Func<Task>> capturedTabs,
        List<TerminalSession> fallbackSessions,
        bool swallowSyncError)
    {
        for (var i = 0; i < 8; i++)
        {
            Func<Task>? extraTab;
            try
            {
                extraTab = tabs.CaptureForRelease(itemId);
            }
            catch when (swallowSyncError)
            {
                extraTab = null;
            }
            if (extraTab is not null)
            {
                capturedTabs.Add(extraTab);
                continue;
            }
            var extraSession = sessions.TakeDetached(itemId);
            if (extraSession is null)
            {
                return;
            }
            fallbackSessions.Add(extraSession);
            try
            {
                sessions.NotifyChanged(itemId);
            }
            catch when (swallowSyncError)
            {
                return;
            }
        }
    }

    private static void StartOrSchedule(Func<Task> release, Action<Func<Task>>? scheduleRelease, bool swallowSyncError)
    {
        try
        {
            if (scheduleRelease is not null)
            {
                scheduleRelease(release);
            }
            else
            {
                var task = release();
                if (swallowSyncError)
                {
                    _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                }
            }
        }
        catch when (swallowSyncError)
        {
            // Never mask the primary observer failure with secondary cleanup errors.
        }
    }

    private static void AddCleanupTask(List<Task> tasks, Func<Task> start, bool swallowSyncError)
    {
        Task task;
        try
        {
            task = start();
        }
        catch when (swallowSyncError)
        {
            return;
        }
        if (!task.IsCompletedSuccessfully && !tasks.Contains(task))
        {
            tasks.Add(task);
        }
    }
}
