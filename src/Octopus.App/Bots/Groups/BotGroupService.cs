using System.Collections.Concurrent;
using Octopus.App.AgentGui;
using Octopus.App.Collab;
using Octopus.App.Workspace;
using Octopus.Core.Bots;
using Octopus.Storage;

namespace Octopus.App.Bots.Groups;

/// <summary>
/// Full runtime for multi-bot Group Rooms inspired by Orca's <c>bot-group-runtime</c>,
/// <c>bot-group-delivery</c>, <c>bot-group-mention-handoffs</c>, and <c>bot-group-stop</c>.
/// </summary>
public sealed class BotGroupService : IBotGroupService, IDisposable
{
    private readonly IBotGroupStore _store;
    private readonly IBotService _bots;
    private readonly Func<ITabContentRegistry?> _tabs;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _groupLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _inFlightDeliveries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _streamingPreviews = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string GroupId, string? ThreadId, string MessageId, string BotId, string ChatId)> _activeOperations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BotGroupPendingApproval> _approvals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AgentGuiTabViewModel> _wiredTabs = new(StringComparer.Ordinal);
    private readonly object _wireGate = new();
    private bool _loaded;

    public BotGroupService(
        IBotGroupStore store,
        IBotService bots,
        Func<ITabContentRegistry?> tabs)
    {
        _store = store;
        _bots = bots;
        _tabs = tabs;
        _bots.Changed += OnBotsChanged;
    }

    /// <summary>Test hook replacing live ACP delivery: (bot, chatId, operationId, promptBody) -> delivered.</summary>
    internal Func<BotDefinition, string, string, string, Task<bool>>? DeliverOverride { get; set; }

    /// <summary>Test hook replacing live ACP turn cancellation: (botId, chatId) -> completed.</summary>
    internal Func<string, string, Task>? StopOverride { get; set; }

    public IReadOnlyList<BotGroupDefinition> Groups
    {
        get
        {
            EnsureLoaded();
            return _store.Current.Groups;
        }
    }

    public BotGroupDefinition? CurrentGroup
    {
        get
        {
            EnsureLoaded();
            var id = _bots.ActiveGroupId;
            return string.IsNullOrWhiteSpace(id) ? null : FindGroup(id);
        }
    }

    public string? CurrentThreadId => CurrentGroup is null ? null : _bots.ActiveGroupThreadId;

    public event EventHandler<BotGroupChangedEventArgs>? Changed;
    public event EventHandler<BotGroupStreamingEventArgs>? StreamingUpdated;

    public void Load()
    {
        _store.Load();
        _loaded = true;
        Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Groups));
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        _ = _store.Current;
    }

    public BotGroupDefinition? FindGroup(string groupId)
    {
        EnsureLoaded();
        return _store.Current.Groups.FirstOrDefault(g => string.Equals(g.Id, groupId, StringComparison.Ordinal));
    }

    public BotGroupDefinition CreateGroup(CreateBotGroupRequest request)
    {
        EnsureLoaded();
        var name = string.IsNullOrWhiteSpace(request.Name) ? "Bot Group" : request.Name.Trim();
        var distinctIds = (request.BotIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(BotGroupDefinition.MaxMembers)
            .ToList();

        var resolvedBots = distinctIds
            .Select(id => _bots.Bots.FirstOrDefault(b => b.Id == id))
            .Where(b => b is not null)
            .Cast<BotDefinition>()
            .ToList();

        if (resolvedBots.Count == 0)
        {
            throw new InvalidOperationException("A bot group must contain at least one existing bot.");
        }

        var groupId = Guid.NewGuid().ToString("N");
        var hostKey = resolvedBots[0].HostDeviceKey;
        var members = new List<BotGroupMember>(resolvedBots.Count);
        foreach (var bot in resolvedBots)
        {
            var chat = _bots.CreateGroupMemberChat(bot.Id, groupId, threadId: null, title: $"{name} (Group)")
                ?? throw new InvalidOperationException($"Could not create group room session for bot '{bot.Name}'.");
            members.Add(new BotGroupMember { BotId = bot.Id, ChatId = chat.Id });
        }

        var replyMode = NormalizeReplyMode(request.ReplyMode, members);
        var group = new BotGroupDefinition
        {
            Id = groupId,
            Name = name,
            HostDeviceKey = hostKey,
            Revision = 1,
            ReplyMode = replyMode,
            MaxHops = Math.Clamp(request.MaxHops <= 0 ? BotGroupDefinition.DefaultMaxHops : request.MaxHops, 1, 30),
            Members = members,
            IsExpanded = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _store.Update(d => d.Groups.Insert(0, group));
        var created = FindGroup(groupId)!;
        _bots.SelectGroup(created.Id);
        Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Groups, created.Id));
        Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Selection, created.Id));
        return created;
    }

    public async Task<BotGroupDefinition> UpdateGroupAsync(UpdateBotGroupRequest request, CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        var gate = GateFor(request.GroupId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = FindGroup(request.GroupId)
                ?? throw new InvalidOperationException("Bot group not found.");
            if (request.Revision > 0 && existing.Revision != request.Revision)
            {
                throw new InvalidOperationException("Group was modified concurrently; please retry.");
            }

            List<string>? targetBotIds = null;
            if (request.BotIds is not null)
            {
                targetBotIds = request.BotIds
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .Take(BotGroupDefinition.MaxMembers)
                    .Where(id => _bots.Bots.Any(b => b.Id == id))
                    .ToList();
                if (targetBotIds.Count == 0)
                {
                    throw new InvalidOperationException("A bot group must keep at least one bot.");
                }
            }

            var newName = string.IsNullOrWhiteSpace(request.Name) ? existing.Name : request.Name.Trim();

            _store.Update(d =>
            {
                if (d.Groups.FirstOrDefault(g => g.Id == request.GroupId) is not { } target) return;
                target.Name = newName;
                if (request.MaxHops is { } hops) target.MaxHops = Math.Clamp(hops, 1, 30);

                if (targetBotIds is not null)
                {
                    SyncRoomMembers(target.Id, null, newName, target.Members, target.FormerChatIds, targetBotIds);
                    foreach (var thread in target.Threads)
                    {
                        var threadLabel = string.IsNullOrWhiteSpace(thread.Title) ? newName : $"{newName} · {thread.Title}";
                        SyncRoomMembers(target.Id, thread.Id, threadLabel, thread.Members, thread.FormerChatIds, targetBotIds);
                    }
                }

                if (request.ReplyMode is not null)
                {
                    target.ReplyMode = NormalizeReplyMode(request.ReplyMode, target.Members);
                }
                else
                {
                    target.ReplyMode = NormalizeReplyMode(target.ReplyMode, target.Members);
                }

                target.Revision++;
            });

            var updated = FindGroup(request.GroupId)!;
            Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Groups, updated.Id));
            return updated;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task DeleteGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        var gate = GateFor(groupId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var group = FindGroup(groupId);
            if (group is null) return;

            await StopRoomLockedAsync(group, null, null, cancellationToken).ConfigureAwait(false);
            foreach (var thread in group.Threads)
            {
                await StopRoomLockedAsync(group, thread.Id, null, cancellationToken).ConfigureAwait(false);
            }

            ClearApprovalsForGroup(groupId);
            _store.Update(d => d.Groups.RemoveAll(g => g.Id == groupId));
            _store.DeleteRoom(groupId);

            if (string.Equals(_bots.ActiveGroupId, groupId, StringComparison.Ordinal))
            {
                _bots.ClearActiveGroup();
            }
            Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Groups, groupId));
            Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Selection));
        }
        finally
        {
            gate.Release();
            _groupLocks.TryRemove(groupId, out _);
        }
    }

    public async Task<BotGroupThread> CreateThreadAsync(string groupId, CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        var gate = GateFor(groupId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var group = FindGroup(groupId)
                ?? throw new InvalidOperationException("Bot group not found.");

            var threadId = Guid.NewGuid().ToString("N");
            var members = new List<BotGroupMember>(group.Members.Count);
            foreach (var m in group.Members)
            {
                var chat = _bots.CreateGroupMemberChat(m.BotId, group.Id, threadId, $"{group.Name} · Thread");
                if (chat is null) continue;
                members.Add(new BotGroupMember { BotId = m.BotId, ChatId = chat.Id });
            }

            var thread = new BotGroupThread
            {
                Id = threadId,
                Members = members,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            _store.Update(d =>
            {
                if (d.Groups.FirstOrDefault(g => g.Id == groupId) is not { } target) return;
                target.Threads.Add(thread);
                target.IsExpanded = true;
                target.Revision++;
            });

            _bots.SelectGroup(groupId, threadId);
            Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Groups, groupId, threadId));
            Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Selection, groupId, threadId));
            return thread;
        }
        finally
        {
            gate.Release();
        }
    }

    public void SelectGroup(string groupId, string? threadId = null)
    {
        EnsureLoaded();
        var group = FindGroup(groupId);
        if (group is null) return;
        if (!string.IsNullOrWhiteSpace(threadId) && group.Threads.All(t => t.Id != threadId))
        {
            threadId = null;
        }
        MarkGroupRead(groupId);
        _bots.SelectGroup(groupId, threadId);
        Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Selection, groupId, threadId));
    }

    public void SetGroupExpanded(string groupId, bool expanded)
    {
        EnsureLoaded();
        var group = FindGroup(groupId);
        if (group is null || group.IsExpanded == expanded) return;
        _store.Update(d =>
        {
            if (d.Groups.FirstOrDefault(g => g.Id == groupId) is { } target)
            {
                target.IsExpanded = expanded;
            }
        });
        Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Groups, groupId));
    }

    public void MarkGroupRead(string groupId)
    {
        EnsureLoaded();
        var group = FindGroup(groupId);
        if (group is null || !group.IsUnread) return;
        _store.Update(d =>
        {
            if (d.Groups.FirstOrDefault(g => g.Id == groupId) is { } target)
            {
                target.IsUnread = false;
            }
        });
        Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Groups, groupId));
    }

    public BotGroupRoomPage ReadRoom(string groupId, string? threadId = null, long? beforeSequence = null, int limit = 60)
    {
        EnsureLoaded();
        var group = FindGroup(groupId)
            ?? throw new InvalidOperationException("Bot group not found.");
        var room = _store.ReadRoom(groupId);
        var normalizedThread = string.IsNullOrWhiteSpace(threadId) ? null : threadId;
        var filtered = room.Messages
            .Where(m => string.Equals(m.ThreadId, normalizedThread, StringComparison.Ordinal))
            .Where(m => beforeSequence is null || m.Sequence < beforeSequence.Value)
            .OrderBy(m => m.Sequence)
            .ToList();

        var clampedLimit = Math.Clamp(limit, 1, 200);
        var hasEarlier = filtered.Count > clampedLimit;
        var page = hasEarlier ? filtered.Skip(filtered.Count - clampedLimit).ToList() : filtered;
        var approvals = GetPendingApprovals(groupId, normalizedThread);

        return new BotGroupRoomPage(group, normalizedThread, page, approvals, hasEarlier);
    }

    public async Task<BotGroupMessage> SendMessageAsync(
        string groupId,
        int revision,
        string text,
        string? threadId = null,
        string? messageId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0) throw new ArgumentException("Message text is required.", nameof(text));

        var normalizedThread = string.IsNullOrWhiteSpace(threadId) ? null : threadId;
        var msgId = string.IsNullOrWhiteSpace(messageId) ? Guid.NewGuid().ToString("N") : messageId.Trim();

        BotGroupMessage stored;
        var isReplay = false;
        var gate = GateFor(groupId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var room = _store.ReadRoom(groupId);
            var existing = room.Messages.FirstOrDefault(m => string.Equals(m.Id, msgId, StringComparison.Ordinal));
            if (existing is not null)
            {
                if (!string.Equals(existing.GroupId, groupId, StringComparison.Ordinal)
                    || !string.Equals(existing.ThreadId, normalizedThread, StringComparison.Ordinal)
                    || !string.Equals(existing.Text, trimmed, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Message id was reused for a different message.");
                }
                stored = existing;
                isReplay = true;
            }
            else
            {
                var group = FindGroup(groupId)
                    ?? throw new InvalidOperationException("Bot group not found.");
                if (revision > 0 && group.Revision != revision)
                {
                    throw new InvalidOperationException("Group changed before send; please retry.");
                }
                if (normalizedThread is not null && group.Threads.All(t => t.Id != normalizedThread))
                {
                    throw new InvalidOperationException("This thread is no longer in the group.");
                }

                var roomMembers = group.GetRoomMembers(normalizedThread);
                if (roomMembers.Count == 0)
                {
                    throw new InvalidOperationException("This group has no members.");
                }

                var namedMembers = ResolveNamedMembers(roomMembers);
                var audience = BotGroupMentionParser.ResolveAudience(trimmed, group.ReplyMode, namedMembers);
                if (!audience.IsValid)
                {
                    throw new InvalidOperationException(audience.Error switch
                    {
                        BotGroupAudienceError.AmbiguousMention => "Mention matches multiple bots with the same name.",
                        BotGroupAudienceError.UnknownLeadingMention => "Unknown @mention at the start of the message.",
                        BotGroupAudienceError.DefaultReplierNotMember => "The default replier is no longer in this group.",
                        _ => "Invalid message audience.",
                    });
                }

                var roomHistory = room.Messages
                    .Where(m => string.Equals(m.ThreadId, normalizedThread, StringComparison.Ordinal))
                    .TakeLast(40)
                    .ToList();

                var reserved = ReserveTurns(roomMembers, roomHistory, audience.BotIds, out var contexts);
                var now = DateTimeOffset.UtcNow;
                BotGroupMessage? appended = null;
                _store.UpdateRoom(groupId, doc =>
                {
                    var seq = doc.NextSequence++;
                    appended = new BotGroupMessage
                    {
                        Id = msgId,
                        GroupId = groupId,
                        ThreadId = normalizedThread,
                        Sequence = seq,
                        IsOwner = true,
                        Text = trimmed,
                        HopDepth = 0,
                        Turns = reserved,
                        CreatedAt = now,
                    };
                    doc.Messages.Add(appended);
                    foreach (var turn in reserved)
                    {
                        doc.PendingDeliveries.Add(new BotGroupPendingDelivery
                        {
                            OperationId = turn.OperationId,
                            MessageId = appended.Id,
                            GroupId = groupId,
                            ThreadId = normalizedThread,
                            ContextJson = contexts.GetValueOrDefault(turn.OperationId, "[]"),
                            CreatedAt = now,
                        });
                    }
                });
                stored = appended!;

                _store.Update(d =>
                {
                    if (d.Groups.FirstOrDefault(g => g.Id == groupId) is not { } target) return;
                    target.IsUnread = false;
                    target.Latest = new BotGroupLatestPreview
                    {
                        AuthorName = null,
                        Text = SummarizePreview(trimmed),
                        At = now,
                    };
                    if (normalizedThread is not null
                        && target.Threads.FirstOrDefault(t => t.Id == normalizedThread) is { } thread
                        && string.IsNullOrWhiteSpace(thread.Title))
                    {
                        thread.Title = SummarizeTitle(trimmed);
                    }
                });
            }
        }
        finally
        {
            gate.Release();
        }

        if (!isReplay)
        {
            Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Messages, groupId, normalizedThread));
            Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Groups, groupId, normalizedThread));
            foreach (var turn in stored.Turns)
            {
                _ = DeliverTurnAsync(groupId, stored.Id, turn.OperationId, cancellationToken);
            }
        }
        return stored;
    }

    public async Task StopAsync(string groupId, string? threadId = null, string? botId = null, CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        var gate = GateFor(groupId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var group = FindGroup(groupId);
            if (group is null) return;
            await StopRoomLockedAsync(group, string.IsNullOrWhiteSpace(threadId) ? null : threadId, botId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
        Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Messages, groupId, threadId));
        Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Groups, groupId, threadId));
    }

    public async Task<bool> ContinueHandoffAsync(string groupId, string pausedMessageId, int extraHops = 3, CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        var toDeliver = new List<(string MessageId, string OperationId)>();
        string? threadId = null;
        var gate = GateFor(groupId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var group = FindGroup(groupId);
            if (group is null) return false;
            var room = _store.ReadRoom(groupId);
            var pausedMsg = room.Messages.FirstOrDefault(m =>
                string.Equals(m.Id, pausedMessageId, StringComparison.Ordinal)
                && m.PausedByHopLimit);
            if (pausedMsg is null || pausedMsg.PausedTargetBotIds is not { Count: > 0 } targetIds) return false;

            threadId = pausedMsg.ThreadId;
            var roomMembers = group.GetRoomMembers(threadId);
            var rootSeq = pausedMsg.EffectiveRootSequence;
            var rootReqId = pausedMsg.EffectiveRootRequestId;
            var activeTargets = targetIds
                .Where(id => IsMemberActiveForRoot(roomMembers, id, rootSeq))
                .ToList();
            if (activeTargets.Count == 0) return false;

            // Locate the source turn that paused so we can quote its reply.
            var sourceTurn = room.Messages
                .SelectMany(m => m.Turns)
                .FirstOrDefault(t => string.Equals(t.OperationId, pausedMsg.PausedSourceOperationId, StringComparison.Ordinal));

            var roomHistory = room.Messages
                .Where(m => string.Equals(m.ThreadId, threadId, StringComparison.Ordinal))
                .TakeLast(40)
                .ToList();

            var reserved = ReserveTurns(roomMembers, roomHistory, activeTargets, out var contexts);
            var now = DateTimeOffset.UtcNow;
            var nextHop = Math.Max(1, pausedMsg.HopDepth - Math.Max(1, extraHops) + 1);

            _store.UpdateRoom(groupId, doc =>
            {
                if (doc.Messages.FirstOrDefault(m => m.Id == pausedMessageId) is { } target)
                {
                    target.PausedByHopLimit = false;
                    target.PausedTargetBotIds = null;
                }
                foreach (var turn in reserved)
                {
                    var lineId = $"handoff-resume:{pausedMessageId}:{turn.BotId}";
                    if (doc.Messages.Any(m => m.Id == lineId)) continue;
                    var msg = new BotGroupMessage
                    {
                        Id = lineId,
                        GroupId = groupId,
                        ThreadId = threadId,
                        Sequence = doc.NextSequence++,
                        IsOwner = false,
                        AuthorBotId = sourceTurn?.BotId ?? pausedMsg.AuthorBotId,
                        AuthorBotName = sourceTurn?.BotName ?? pausedMsg.AuthorBotName,
                        Text = $"{sourceTurn?.BotName ?? "Bot"} asked {turn.BotName} (continued)",
                        RootRequestId = rootReqId,
                        RootSequence = rootSeq,
                        HopDepth = nextHop,
                        RequesterBotId = sourceTurn?.BotId ?? pausedMsg.RequesterBotId,
                        RequesterBotName = sourceTurn?.BotName ?? pausedMsg.RequesterBotName,
                        IsMentionHandoff = true,
                        Turns = [turn],
                        CreatedAt = now,
                    };
                    doc.Messages.Add(msg);
                    doc.PendingDeliveries.Add(new BotGroupPendingDelivery
                    {
                        OperationId = turn.OperationId,
                        MessageId = msg.Id,
                        GroupId = groupId,
                        ThreadId = threadId,
                        ContextJson = contexts.GetValueOrDefault(turn.OperationId, "[]"),
                        CreatedAt = now,
                    });
                    toDeliver.Add((msg.Id, turn.OperationId));
                }
            });
        }
        finally
        {
            gate.Release();
        }

        if (toDeliver.Count == 0) return false;
        Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Messages, groupId, threadId));
        foreach (var (msgId, opId) in toDeliver)
        {
            _ = DeliverTurnAsync(groupId, msgId, opId, cancellationToken);
        }
        return true;
    }

    public async Task ResumePendingDeliveriesAsync(CancellationToken cancellationToken = default)
    {
        EnsureLoaded();
        foreach (var group in Groups.ToList())
        {
            var room = _store.ReadRoom(group.Id);
            // Settle orphaned Running turns left behind by a previous process crash.
            var repaired = false;
            var pendingOps = room.PendingDeliveries.Select(p => p.OperationId).ToHashSet(StringComparer.Ordinal);
            _store.UpdateRoom(group.Id, doc =>
            {
                foreach (var msg in doc.Messages)
                {
                    foreach (var turn in msg.Turns)
                    {
                        if (turn.Status == BotGroupTurnStatus.Running && !pendingOps.Contains(turn.OperationId))
                        {
                            turn.Status = BotGroupTurnStatus.Stopped;
                            turn.Error ??= "Interrupted before completion.";
                            turn.CompletedAt ??= DateTimeOffset.UtcNow;
                            repaired = true;
                        }
                    }
                }
            });
            if (repaired)
            {
                Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Messages, group.Id));
            }

            foreach (var pending in room.PendingDeliveries.ToList())
            {
                await DeliverTurnAsync(group.Id, pending.MessageId, pending.OperationId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public IReadOnlyList<BotGroupPendingApproval> GetPendingApprovals(string? groupId = null, string? threadId = null)
    {
        var query = _approvals.Values.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(groupId))
        {
            query = query.Where(a => string.Equals(a.GroupId, groupId, StringComparison.Ordinal));
            if (threadId is not null)
            {
                query = query.Where(a => string.Equals(a.ThreadId, threadId, StringComparison.Ordinal));
            }
        }
        return query.ToList();
    }

    public bool RespondApproval(string approvalId, string? optionId)
    {
        if (!_approvals.TryGetValue(approvalId, out var approval)) return false;
        var ok = approval.Respond(optionId);
        if (ok)
        {
            _approvals.TryRemove(approvalId, out _);
            Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Approvals, approval.GroupId, approval.ThreadId));
            Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Groups, approval.GroupId, approval.ThreadId));
        }
        return ok;
    }

    public string? GetStreamingPreview(string operationId) =>
        _streamingPreviews.TryGetValue(operationId, out var text) ? text : null;

    public int TotalPendingApprovalCount() => _approvals.Count;

    public bool HasWorkingGroups() => Groups.Any(g => IsGroupWorking(g.Id));

    public bool IsGroupWorking(string groupId)
    {
        if (_activeOperations.Values.Any(v => string.Equals(v.GroupId, groupId, StringComparison.Ordinal)))
        {
            return true;
        }
        var room = _store.ReadRoom(groupId);
        return room.Messages.Any(m => m.Turns.Any(t => t.Status is BotGroupTurnStatus.Queued or BotGroupTurnStatus.Running));
    }

    public void OpenMemberSessionInspector(string botId, string chatId)
    {
        _bots.OpenChat(botId, chatId);
    }

    /// <summary>
    /// Internal test/runtime entrypoint to settle a turn's completion directly (used both by live AgentGuiTabViewModel hooks and unit tests).
    /// </summary>
    internal async Task HandleTurnSettledAsync(
        string chatId,
        string? operationId,
        string? finalMarkdown,
        bool cancelled,
        string? error)
    {
        var resolvedOpId = operationId;
        if (string.IsNullOrWhiteSpace(resolvedOpId))
        {
            resolvedOpId = _activeOperations
                .FirstOrDefault(kv => string.Equals(kv.Value.ChatId, chatId, StringComparison.Ordinal))
                .Key;
        }
        if (string.IsNullOrWhiteSpace(resolvedOpId)) return;
        if (!_activeOperations.TryRemove(resolvedOpId, out var meta)) return;

        _streamingPreviews.TryRemove(resolvedOpId, out _);

        var clippedReply = BotGroupContextBuilder.ClipReplyForHistory(finalMarkdown);
        BotGroupMessage? updatedMessage = null;
        BotGroupTurn? updatedTurn = null;

        var gate = GateFor(meta.GroupId);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _store.UpdateRoom(meta.GroupId, doc =>
            {
                var msg = doc.Messages.FirstOrDefault(m => m.Id == meta.MessageId);
                var turn = msg?.Turns.FirstOrDefault(t => t.OperationId == resolvedOpId);
                if (msg is null || turn is null) return;

                if (cancelled)
                {
                    turn.Status = BotGroupTurnStatus.Stopped;
                    if (!string.IsNullOrWhiteSpace(clippedReply)) turn.Reply = clippedReply;
                }
                else if (!string.IsNullOrWhiteSpace(error))
                {
                    turn.Status = BotGroupTurnStatus.Failed;
                    turn.Error = error;
                }
                else
                {
                    turn.Status = BotGroupTurnStatus.Completed;
                    turn.Reply = clippedReply;
                    turn.Error = null;
                }
                turn.CompletedAt = DateTimeOffset.UtcNow;
                updatedMessage = msg;
                updatedTurn = turn;
            });

            if (updatedTurn is { Status: BotGroupTurnStatus.Completed, Reply.Length: > 0 })
            {
                var isVisible = string.Equals(_bots.ActiveGroupId, meta.GroupId, StringComparison.Ordinal)
                    && string.Equals(_bots.ActiveGroupThreadId, meta.ThreadId, StringComparison.Ordinal);
                _store.Update(d =>
                {
                    if (d.Groups.FirstOrDefault(g => g.Id == meta.GroupId) is not { } g) return;
                    g.Latest = new BotGroupLatestPreview
                    {
                        AuthorName = updatedTurn.BotName,
                        Text = SummarizePreview(updatedTurn.Reply!),
                        At = updatedTurn.CompletedAt ?? DateTimeOffset.UtcNow,
                    };
                    if (!isVisible) g.IsUnread = true;
                });
            }
        }
        finally
        {
            gate.Release();
        }

        Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Messages, meta.GroupId, meta.ThreadId));
        Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Groups, meta.GroupId, meta.ThreadId));

        if (updatedMessage is not null
            && updatedTurn is { Status: BotGroupTurnStatus.Completed }
            && !string.IsNullOrWhiteSpace(finalMarkdown))
        {
            await HandOffMentionsAsync(meta.GroupId, updatedMessage.Id, updatedTurn.OperationId, finalMarkdown!).ConfigureAwait(false);
        }
    }

    private async Task DeliverTurnAsync(string groupId, string messageId, string operationId, CancellationToken cancellationToken)
    {
        if (!_inFlightDeliveries.TryAdd(operationId, 0)) return;
        string? threadId = null;
        try
        {
            var group = FindGroup(groupId);
            var room = _store.ReadRoom(groupId);
            var pending = room.PendingDeliveries.FirstOrDefault(p => p.OperationId == operationId);
            var message = room.Messages.FirstOrDefault(m => m.Id == messageId);
            var turn = message?.Turns.FirstOrDefault(t => t.OperationId == operationId);
            if (group is null || pending is null || message is null || turn is null)
            {
                ClearPending(groupId, operationId);
                return;
            }

            threadId = message.ThreadId;
            var roomMembers = group.GetRoomMembers(threadId);
            if (!IsTurnActive(roomMembers, message, turn, out var inactiveReason))
            {
                MarkTurnFailedOrStopped(groupId, messageId, operationId, stopped: true, inactiveReason);
                ClearPending(groupId, operationId);
                return;
            }

            var bot = _bots.Bots.FirstOrDefault(b => b.Id == turn.BotId);
            if (bot is null || bot.IsPaused)
            {
                MarkTurnFailedOrStopped(groupId, messageId, operationId, stopped: false, "Bot is paused or unavailable.");
                ClearPending(groupId, operationId);
                return;
            }

            var original = message.IsMentionHandoff && !string.IsNullOrWhiteSpace(message.RootRequestId)
                ? room.Messages.FirstOrDefault(m => m.Id == message.RootRequestId)
                : null;
            var memberNames = ResolveNamedMembers(roomMembers).Select(m => m.Name).ToList();
            var promptEnvelope = BotGroupContextBuilder.BuildTurnPromptEnvelope(
                group.Name,
                bot.Name,
                memberNames,
                pending.ContextJson,
                message,
                original);

            _activeOperations[operationId] = (groupId, threadId, messageId, bot.Id, turn.ChatId);
            MarkTurnRunning(groupId, messageId, operationId);

            bool delivered;
            if (DeliverOverride is { } over)
            {
                delivered = await over(bot, turn.ChatId, operationId, promptEnvelope).ConfigureAwait(false);
            }
            else
            {
                delivered = await DeliverToLiveAgentTabAsync(groupId, threadId, bot, turn.ChatId, operationId, promptEnvelope, cancellationToken).ConfigureAwait(false);
            }

            if (!delivered)
            {
                _activeOperations.TryRemove(operationId, out _);
                MarkTurnFailedOrStopped(groupId, messageId, operationId, stopped: false, "The bot session did not accept the prompt.");
            }
            ClearPending(groupId, operationId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _activeOperations.TryRemove(operationId, out _);
            MarkTurnFailedOrStopped(groupId, messageId, operationId, stopped: false, ex.Message);
            ClearPending(groupId, operationId);
        }
        finally
        {
            _inFlightDeliveries.TryRemove(operationId, out _);
            Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Messages, groupId, threadId));
        }
    }

    private async Task<bool> DeliverToLiveAgentTabAsync(
        string groupId,
        string? threadId,
        BotDefinition bot,
        string chatId,
        string operationId,
        string promptEnvelope,
        CancellationToken cancellationToken)
    {
        var item = _bots.GetGroupMemberBackingItem(bot.Id, chatId);
        if (item is null) return false;
        var backing = _bots.GetBackingProject(bot);
        var registry = _tabs();
        if (registry is null) return false;

        var control = registry.GetOrCreate(backing, item);
        if (control?.DataContext is not ICollabSessionBridge bridge)
        {
            return false;
        }
        if (control.DataContext is AgentGuiTabViewModel guiVm)
        {
            WireAgentTab(groupId, threadId, bot, chatId, guiVm);
        }

        var roomKey = $"bot-group:{groupId}:{threadId ?? "main"}";
        return await bridge.DeliverCollabPromptAsync(promptEnvelope, threadId: roomKey, taskId: operationId, cancellationToken).ConfigureAwait(false);
    }

    private void WireAgentTab(string groupId, string? threadId, BotDefinition bot, string chatId, AgentGuiTabViewModel vm)
    {
        lock (_wireGate)
        {
            if (_wiredTabs.TryGetValue(chatId, out var existing) && ReferenceEquals(existing, vm))
            {
                SyncExistingApprovals(groupId, threadId, bot, chatId, vm);
                return;
            }
            _wiredTabs[chatId] = vm;
            vm.TurnStarted += (itemId, opId) =>
            {
                if (!string.IsNullOrWhiteSpace(opId) && _activeOperations.TryGetValue(opId, out var meta))
                {
                    MarkTurnRunning(meta.GroupId, meta.MessageId, opId);
                    Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Messages, meta.GroupId, meta.ThreadId));
                }
            };
            vm.TurnStreaming += (itemId, opId, markdown) =>
            {
                var targetOp = !string.IsNullOrWhiteSpace(opId)
                    ? opId
                    : _activeOperations.FirstOrDefault(kv => kv.Value.ChatId == itemId).Key;
                if (string.IsNullOrWhiteSpace(targetOp) || !_activeOperations.TryGetValue(targetOp, out var meta)) return;
                _streamingPreviews[targetOp] = markdown;
                StreamingUpdated?.Invoke(this, new BotGroupStreamingEventArgs(meta.GroupId, meta.ThreadId, targetOp, markdown));
            };
            vm.TurnFinished += (itemId, opId, finalMarkdown, cancelled, error) =>
            {
                _ = HandleTurnSettledAsync(itemId, opId, finalMarkdown, cancelled, error);
            };
            vm.PermissionApprovalAdded += approval =>
            {
                var currentBot = _bots.Bots.FirstOrDefault(b => b.Id == bot.Id) ?? bot;
                var wrapped = new BotGroupPendingApproval(
                    approval.ApprovalId,
                    groupId,
                    threadId,
                    currentBot.Id,
                    currentBot.Name,
                    currentBot.Color,
                    chatId,
                    approval.Title,
                    approval.Detail,
                    approval.WarningMessage,
                    approval.Options,
                    approval.TryRespond);
                _approvals[approval.ApprovalId] = wrapped;
                Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Approvals, groupId, threadId));
                Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Groups, groupId, threadId));
            };
            vm.PermissionApprovalCleared += approvalId =>
            {
                if (_approvals.TryRemove(approvalId, out var removed))
                {
                    Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Approvals, removed.GroupId, removed.ThreadId));
                    Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Groups, removed.GroupId, removed.ThreadId));
                }
            };
            SyncExistingApprovals(groupId, threadId, bot, chatId, vm);
        }
    }

    private void SyncExistingApprovals(string groupId, string? threadId, BotDefinition bot, string chatId, AgentGuiTabViewModel vm)
    {
        foreach (var approval in vm.PendingApprovals)
        {
            if (approval.IsResolved || _approvals.ContainsKey(approval.ApprovalId)) continue;
            _approvals[approval.ApprovalId] = new BotGroupPendingApproval(
                approval.ApprovalId,
                groupId,
                threadId,
                bot.Id,
                bot.Name,
                bot.Color,
                chatId,
                approval.Title,
                approval.Detail,
                approval.WarningMessage,
                approval.Options,
                approval.TryRespond);
        }
    }

    private async Task HandOffMentionsAsync(string groupId, string sourceMessageId, string sourceOperationId, string fullReply)
    {
        var toDeliver = new List<(string MessageId, string OperationId)>();
        string? threadId = null;

        var gate = GateFor(groupId);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var group = FindGroup(groupId);
            if (group is null) return;
            var room = _store.ReadRoom(groupId);
            var sourceMessage = room.Messages.FirstOrDefault(m => m.Id == sourceMessageId);
            var sourceTurn = sourceMessage?.Turns.FirstOrDefault(t => t.OperationId == sourceOperationId);
            if (sourceMessage is null || sourceTurn is null || sourceTurn.HandedOff is not null) return;

            threadId = sourceMessage.ThreadId;
            var rootReqId = sourceMessage.EffectiveRootRequestId;
            var rootSeq = sourceMessage.EffectiveRootSequence;
            var roomMembers = group.GetRoomMembers(threadId);

            // Speaker must still be an active member past Stop watermark.
            if (!IsMemberActiveForRoot(roomMembers, sourceTurn.BotId, rootSeq, sourceTurn.ChatId))
            {
                MarkHandedOff(groupId, sourceMessageId, sourceOperationId, []);
                return;
            }

            var namedMembers = ResolveNamedMembers(roomMembers);
            var mentioned = BotGroupMentionParser.ExtractReplyMentions(fullReply, namedMembers, sourceTurn.BotId)
                .Where(m => IsMemberActiveForRoot(roomMembers, m.BotId, rootSeq))
                .Where(m => _bots.Bots.Any(b => b.Id == m.BotId && !b.IsPaused))
                .ToList();

            var targetIds = mentioned.Select(m => m.BotId).ToList();
            if (targetIds.Count == 0)
            {
                MarkHandedOff(groupId, sourceMessageId, sourceOperationId, []);
                return;
            }

            var nextHop = sourceMessage.HopDepth + 1;
            if (nextHop > group.MaxHops)
            {
                var now = DateTimeOffset.UtcNow;
                _store.UpdateRoom(groupId, doc =>
                {
                    if (doc.Messages.FirstOrDefault(m => m.Id == sourceMessageId)?.Turns.FirstOrDefault(t => t.OperationId == sourceOperationId) is { } st)
                    {
                        st.HandedOff = targetIds;
                    }
                    var pauseId = $"hop-limit:{sourceOperationId}";
                    if (doc.Messages.Any(m => m.Id == pauseId)) return;
                    doc.Messages.Add(new BotGroupMessage
                    {
                        Id = pauseId,
                        GroupId = groupId,
                        ThreadId = threadId,
                        Sequence = doc.NextSequence++,
                        IsOwner = false,
                        AuthorBotId = sourceTurn.BotId,
                        AuthorBotName = sourceTurn.BotName,
                        Text = $"Auto handoff paused after {group.MaxHops} hops.",
                        RootRequestId = rootReqId,
                        RootSequence = rootSeq,
                        HopDepth = nextHop,
                        RequesterBotId = sourceTurn.BotId,
                        RequesterBotName = sourceTurn.BotName,
                        PausedByHopLimit = true,
                        PausedTargetBotIds = targetIds,
                        PausedSourceOperationId = sourceOperationId,
                        CreatedAt = now,
                    });
                });
                Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Messages, groupId, threadId));
                return;
            }

            var roomHistory = room.Messages
                .Where(m => string.Equals(m.ThreadId, threadId, StringComparison.Ordinal))
                .TakeLast(40)
                .ToList();
            var reserved = ReserveTurns(roomMembers, roomHistory, targetIds, out var contexts);

            // Guarantee each woken member sees the naming excerpt around its @mention.
            foreach (var wokenTurn in reserved)
            {
                var mentionStart = mentioned.FirstOrDefault(m => m.BotId == wokenTurn.BotId)?.Start ?? 0;
                var baseCtx = contexts.GetValueOrDefault(wokenTurn.OperationId, "[]");
                contexts[wokenTurn.OperationId] = BotGroupContextBuilder.WithNamingReply(baseCtx, sourceTurn, fullReply, mentionStart);
            }

            var handoffNow = DateTimeOffset.UtcNow;
            _store.UpdateRoom(groupId, doc =>
            {
                if (doc.Messages.FirstOrDefault(m => m.Id == sourceMessageId)?.Turns.FirstOrDefault(t => t.OperationId == sourceOperationId) is { } st)
                {
                    st.HandedOff = targetIds;
                }
                foreach (var wokenTurn in reserved)
                {
                    var lineId = $"handoff:{sourceOperationId}:{wokenTurn.BotId}";
                    if (doc.Messages.Any(m => m.Id == lineId)) continue;
                    var msg = new BotGroupMessage
                    {
                        Id = lineId,
                        GroupId = groupId,
                        ThreadId = threadId,
                        Sequence = doc.NextSequence++,
                        IsOwner = false,
                        AuthorBotId = sourceTurn.BotId,
                        AuthorBotName = sourceTurn.BotName,
                        Text = $"{sourceTurn.BotName} asked {wokenTurn.BotName}",
                        RootRequestId = rootReqId,
                        RootSequence = rootSeq,
                        HopDepth = nextHop,
                        RequesterBotId = sourceTurn.BotId,
                        RequesterBotName = sourceTurn.BotName,
                        IsMentionHandoff = true,
                        Turns = [wokenTurn],
                        CreatedAt = handoffNow,
                    };
                    doc.Messages.Add(msg);
                    doc.PendingDeliveries.Add(new BotGroupPendingDelivery
                    {
                        OperationId = wokenTurn.OperationId,
                        MessageId = msg.Id,
                        GroupId = groupId,
                        ThreadId = threadId,
                        ContextJson = contexts.GetValueOrDefault(wokenTurn.OperationId, "[]"),
                        CreatedAt = handoffNow,
                    });
                    toDeliver.Add((msg.Id, wokenTurn.OperationId));
                }
            });
        }
        finally
        {
            gate.Release();
        }

        if (toDeliver.Count > 0)
        {
            Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Messages, groupId, threadId));
            foreach (var (msgId, opId) in toDeliver)
            {
                _ = DeliverTurnAsync(groupId, msgId, opId, CancellationToken.None);
            }
        }
    }

    private async Task StopRoomLockedAsync(BotGroupDefinition group, string? threadId, string? botId, CancellationToken cancellationToken)
    {
        var room = _store.ReadRoom(group.Id);
        var maxSeq = room.Messages
            .Where(m => string.Equals(m.ThreadId, threadId, StringComparison.Ordinal))
            .Select(m => m.Sequence)
            .DefaultIfEmpty(0)
            .Max();

        var affectedMembers = group.GetRoomMembers(threadId)
            .Where(m => string.IsNullOrWhiteSpace(botId) || string.Equals(m.BotId, botId, StringComparison.Ordinal))
            .ToList();
        var affectedChatIds = affectedMembers.Select(m => m.ChatId).ToHashSet(StringComparer.Ordinal);

        _store.Update(d =>
        {
            if (d.Groups.FirstOrDefault(g => g.Id == group.Id) is not { } target) return;
            var list = target.GetRoomMembers(threadId);
            foreach (var m in list)
            {
                if (string.IsNullOrWhiteSpace(botId) || string.Equals(m.BotId, botId, StringComparison.Ordinal))
                {
                    m.StoppedThroughSequence = Math.Max(m.StoppedThroughSequence, maxSeq);
                }
            }
        });

        var roomKey = $"bot-group:{group.Id}:{threadId ?? "main"}";
        _store.UpdateRoom(group.Id, doc =>
        {
            foreach (var msg in doc.Messages.Where(m => string.Equals(m.ThreadId, threadId, StringComparison.Ordinal)))
            {
                foreach (var turn in msg.Turns)
                {
                    if (!affectedChatIds.Contains(turn.ChatId)) continue;
                    if (turn.Status is BotGroupTurnStatus.Queued or BotGroupTurnStatus.Running)
                    {
                        turn.Status = BotGroupTurnStatus.Stopped;
                        turn.CompletedAt = DateTimeOffset.UtcNow;
                        _activeOperations.TryRemove(turn.OperationId, out _);
                        _streamingPreviews.TryRemove(turn.OperationId, out _);
                        doc.PendingDeliveries.RemoveAll(p => p.OperationId == turn.OperationId);
                    }
                }
            }
        });

        foreach (var member in affectedMembers)
        {
            if (StopOverride is { } stopOver)
            {
                await stopOver(member.BotId, member.ChatId).ConfigureAwait(false);
                continue;
            }
            var control = _tabs()?.Find(member.ChatId);
            if (control is Avalonia.Controls.Control avaloniaControl && avaloniaControl.DataContext is ICollabSessionBridge bridge)
            {
                bridge.CancelQueuedCollabPrompts(threadId: roomKey);
                await bridge.CancelActiveTurnAsync().ConfigureAwait(false);
            }
        }
    }

    private List<BotGroupTurn> ReserveTurns(
        IReadOnlyList<BotGroupMember> roomMembers,
        IReadOnlyList<BotGroupMessage> roomHistory,
        IReadOnlyList<string> targetBotIds,
        out Dictionary<string, string> contexts)
    {
        contexts = new Dictionary<string, string>(StringComparer.Ordinal);
        var turns = new List<BotGroupTurn>(targetBotIds.Count);
        foreach (var botId in targetBotIds)
        {
            var member = roomMembers.FirstOrDefault(m => string.Equals(m.BotId, botId, StringComparison.Ordinal));
            var bot = _bots.Bots.FirstOrDefault(b => string.Equals(b.Id, botId, StringComparison.Ordinal));
            if (member is null || bot is null) continue;

            var opId = Guid.NewGuid().ToString("N");
            contexts[opId] = BotGroupContextBuilder.BuildIncrementalContextJson(roomHistory, member);
            turns.Add(new BotGroupTurn
            {
                OperationId = opId,
                BotId = bot.Id,
                BotName = bot.Name,
                BotColor = bot.Color,
                BotIcon = bot.Icon,
                AgentId = bot.AgentId,
                ChatId = member.ChatId,
                Status = BotGroupTurnStatus.Queued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        return turns;
    }

    private void SyncRoomMembers(
        string groupId,
        string? threadId,
        string roomLabel,
        List<BotGroupMember> members,
        List<string> formerChatIds,
        List<string> desiredBotIds)
    {
        var desiredSet = desiredBotIds.ToHashSet(StringComparer.Ordinal);
        for (var i = members.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(members[i].BotId))
            {
                if (!formerChatIds.Contains(members[i].ChatId, StringComparer.Ordinal))
                {
                    formerChatIds.Add(members[i].ChatId);
                }
                members.RemoveAt(i);
            }
        }
        foreach (var botId in desiredBotIds)
        {
            if (members.Any(m => string.Equals(m.BotId, botId, StringComparison.Ordinal))) continue;
            var chat = _bots.CreateGroupMemberChat(botId, groupId, threadId, $"{roomLabel} (Group)");
            if (chat is not null)
            {
                members.Add(new BotGroupMember { BotId = botId, ChatId = chat.Id });
            }
        }
    }

    private void OnBotsChanged(object? sender, BotChangedEventArgs e)
    {
        if (e.Kind != BotChangeKind.Bots || e.Bot is null) return;
        // If a bot was deleted, cascade-remove it from every group (preserving former chat ids for history).
        var removedId = e.Bot.Id;
        if (_bots.Bots.Any(b => b.Id == removedId)) return;

        var changedGroups = new List<string>();
        _store.Update(d =>
        {
            foreach (var group in d.Groups)
            {
                var touched = RemoveBotFromMemberList(group.Members, group.FormerChatIds, removedId);
                foreach (var thread in group.Threads)
                {
                    touched |= RemoveBotFromMemberList(thread.Members, thread.FormerChatIds, removedId);
                }
                if (touched)
                {
                    group.ReplyMode = NormalizeReplyMode(group.ReplyMode, group.Members);
                    group.Revision++;
                    changedGroups.Add(group.Id);
                }
            }
        });

        foreach (var gid in changedGroups)
        {
            Changed?.Invoke(this, new BotGroupChangedEventArgs(BotGroupChangeKind.Groups, gid));
        }
    }

    private static bool RemoveBotFromMemberList(List<BotGroupMember> members, List<string> former, string botId)
    {
        var removed = false;
        for (var i = members.Count - 1; i >= 0; i--)
        {
            if (string.Equals(members[i].BotId, botId, StringComparison.Ordinal))
            {
                if (!former.Contains(members[i].ChatId, StringComparer.Ordinal))
                {
                    former.Add(members[i].ChatId);
                }
                members.RemoveAt(i);
                removed = true;
            }
        }
        return removed;
    }

    private IReadOnlyList<BotGroupNamedMember> ResolveNamedMembers(IReadOnlyList<BotGroupMember> members)
    {
        var list = new List<BotGroupNamedMember>(members.Count);
        foreach (var m in members)
        {
            var bot = _bots.Bots.FirstOrDefault(b => b.Id == m.BotId);
            if (bot is not null)
            {
                list.Add(new BotGroupNamedMember(bot.Id, bot.Name));
            }
        }
        return list;
    }

    private static bool IsMemberActiveForRoot(
        IReadOnlyList<BotGroupMember> roomMembers,
        string botId,
        long rootSequence,
        string? requiredChatId = null) =>
        roomMembers.Any(m =>
            string.Equals(m.BotId, botId, StringComparison.Ordinal)
            && (requiredChatId is null || string.Equals(m.ChatId, requiredChatId, StringComparison.Ordinal))
            && m.StoppedThroughSequence < rootSequence);

    private static bool IsTurnActive(
        IReadOnlyList<BotGroupMember> roomMembers,
        BotGroupMessage message,
        BotGroupTurn turn,
        out string reason)
    {
        var member = roomMembers.FirstOrDefault(m => string.Equals(m.ChatId, turn.ChatId, StringComparison.Ordinal));
        if (member is null)
        {
            reason = "Bot is no longer a member of this group.";
            return false;
        }
        if (member.StoppedThroughSequence >= message.EffectiveRootSequence)
        {
            reason = "Stopped by owner.";
            return false;
        }
        reason = "";
        return true;
    }

    private void MarkTurnRunning(string groupId, string messageId, string operationId)
    {
        _store.UpdateRoom(groupId, doc =>
        {
            if (doc.Messages.FirstOrDefault(m => m.Id == messageId)?.Turns.FirstOrDefault(t => t.OperationId == operationId) is { } turn
                && turn.Status == BotGroupTurnStatus.Queued)
            {
                turn.Status = BotGroupTurnStatus.Running;
            }
        });
    }

    private void MarkTurnFailedOrStopped(string groupId, string messageId, string operationId, bool stopped, string? error)
    {
        _store.UpdateRoom(groupId, doc =>
        {
            if (doc.Messages.FirstOrDefault(m => m.Id == messageId)?.Turns.FirstOrDefault(t => t.OperationId == operationId) is { } turn)
            {
                turn.Status = stopped ? BotGroupTurnStatus.Stopped : BotGroupTurnStatus.Failed;
                turn.Error = error;
                turn.CompletedAt = DateTimeOffset.UtcNow;
            }
        });
    }

    private void MarkHandedOff(string groupId, string messageId, string operationId, List<string> targets)
    {
        _store.UpdateRoom(groupId, doc =>
        {
            if (doc.Messages.FirstOrDefault(m => m.Id == messageId)?.Turns.FirstOrDefault(t => t.OperationId == operationId) is { } turn)
            {
                turn.HandedOff = targets;
            }
        });
    }

    private void ClearPending(string groupId, string operationId)
    {
        _store.UpdateRoom(groupId, doc => doc.PendingDeliveries.RemoveAll(p => p.OperationId == operationId));
    }

    private void ClearApprovalsForGroup(string groupId)
    {
        foreach (var kv in _approvals.Where(k => string.Equals(k.Value.GroupId, groupId, StringComparison.Ordinal)).ToList())
        {
            _approvals.TryRemove(kv.Key, out _);
        }
    }

    private static BotGroupReplyMode NormalizeReplyMode(BotGroupReplyMode? mode, IReadOnlyList<BotGroupMember> members)
    {
        if (mode is null) return new BotGroupReplyMode { Kind = BotGroupReplyModeKind.Mentions };
        if (mode.Kind == BotGroupReplyModeKind.Bot
            && (string.IsNullOrWhiteSpace(mode.BotId) || members.All(m => m.BotId != mode.BotId)))
        {
            return new BotGroupReplyMode { Kind = BotGroupReplyModeKind.Mentions };
        }
        return mode.Clone();
    }

    private static string SummarizePreview(string text)
    {
        var single = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return single.Length > 200 ? single[..200] + "…" : single;
    }

    private static string SummarizeTitle(string text)
    {
        var firstLine = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? text.Trim();
        return firstLine.Length > 80 ? firstLine[..80] : firstLine;
    }

    private SemaphoreSlim GateFor(string groupId) =>
        _groupLocks.GetOrAdd(groupId, _ => new SemaphoreSlim(1, 1));

    public void Dispose()
    {
        _bots.Changed -= OnBotsChanged;
        foreach (var gate in _groupLocks.Values)
        {
            gate.Dispose();
        }
        _groupLocks.Clear();
    }
}
