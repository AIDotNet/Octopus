using Octopus.Mobile.Workspace;
using Octopus.Remote.Protocol;

namespace Octopus.Mobile.Remote;

internal sealed partial class RemoteWorkspace
{
    public bool SupportsBotGroups(RemoteHost host) =>
        _connections.ClientOf(host.Id) is { SupportsBotGroups: true };

    public async Task<RemoteBotGroup> CreateBotGroupAsync(RemoteHost host, CreateRemoteBotGroupRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(host.Id) ?? await _connections.ConnectAsync(host.Id).ConfigureAwait(false);
            if (!client.SupportsBotGroups)
            {
                throw new IOException("This computer needs an update to support Bot Groups.");
            }
            var created = await client.CreateBotGroupAsync(new CreateBotGroupMessage
            {
                Name = request.Name,
                BotIds = [.. request.BotIds],
                ReplyModeKind = request.ReplyModeKind,
                ReplyModeBotId = request.ReplyModeBotId,
            }, cancellationToken).ConfigureAwait(false);
            return await UiAsync(() => UpsertBotGroup(host, created)).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task<RemoteBotGroup> UpdateBotGroupAsync(RemoteBotGroup group, UpdateRemoteBotGroupRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(group.Host.Id) ?? await _connections.ConnectAsync(group.Host.Id).ConfigureAwait(false);
            var updated = await client.UpdateBotGroupAsync(new UpdateBotGroupMessage
            {
                GroupId = request.GroupId,
                Revision = request.Revision,
                Name = request.Name,
                BotIds = request.BotIds is null ? null : [.. request.BotIds],
                ReplyModeKind = request.ReplyModeKind,
                ReplyModeBotId = request.ReplyModeBotId,
                MaxHops = request.MaxHops,
            }, cancellationToken).ConfigureAwait(false);
            return await UiAsync(() => UpsertBotGroup(group.Host, updated)).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task DeleteBotGroupAsync(RemoteBotGroup group, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(group.Host.Id) ?? await _connections.ConnectAsync(group.Host.Id).ConfigureAwait(false);
            await client.DeleteBotGroupAsync(group.Id, cancellationToken).ConfigureAwait(false);
            await UiAsync(() => group.Host.BotGroups.Remove(group)).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task<RemoteBotGroupRoomSnapshot> ReadBotGroupRoomAsync(
        RemoteBotGroup group,
        string? threadId = null,
        long? beforeSequence = null,
        int limit = 60,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(group.Host.Id) ?? await _connections.ConnectAsync(group.Host.Id).ConfigureAwait(false);
            var snapshot = await client.ReadBotGroupRoomAsync(new BotGroupRoomReadRequestMessage
            {
                GroupId = group.Id,
                ThreadId = threadId,
                BeforeSequence = beforeSequence,
                Limit = limit,
            }, cancellationToken).ConfigureAwait(false);
            var syncedGroup = await UiAsync(() => UpsertBotGroup(group.Host, snapshot.Group)).ConfigureAwait(false);
            var entries = snapshot.Messages.Select(MapBotGroupEntry).ToList();
            var approvals = snapshot.Approvals.Select(MapBotGroupApproval).ToList();
            return new RemoteBotGroupRoomSnapshot(syncedGroup, snapshot.ThreadId, entries, approvals, snapshot.HasEarlier);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task<RemoteBotGroupEntry> SendBotGroupMessageAsync(
        RemoteBotGroup group,
        string text,
        string? threadId = null,
        string? messageId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(group.Host.Id) ?? await _connections.ConnectAsync(group.Host.Id).ConfigureAwait(false);
            var entry = await client.SendBotGroupMessageAsync(new BotGroupSendMessage
            {
                GroupId = group.Id,
                ThreadId = threadId,
                Revision = group.Revision,
                MessageId = messageId ?? Guid.NewGuid().ToString("N"),
                Text = text,
            }, cancellationToken).ConfigureAwait(false);
            return MapBotGroupEntry(entry);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task StopBotGroupAsync(
        RemoteBotGroup group,
        string? threadId = null,
        string? botId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(group.Host.Id) ?? await _connections.ConnectAsync(group.Host.Id).ConfigureAwait(false);
            await client.StopBotGroupAsync(new BotGroupStopMessage
            {
                GroupId = group.Id,
                ThreadId = threadId,
                BotId = botId,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task ContinueBotGroupHandoffAsync(
        RemoteBotGroup group,
        string messageId,
        int extraHops = 3,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(group.Host.Id) ?? await _connections.ConnectAsync(group.Host.Id).ConfigureAwait(false);
            await client.ContinueBotGroupHandoffAsync(new BotGroupContinueHandoffMessage
            {
                GroupId = group.Id,
                MessageId = messageId,
                ExtraHops = extraHops,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task<RemoteBotGroupThread> CreateBotGroupThreadAsync(RemoteBotGroup group, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(group.Host.Id) ?? await _connections.ConnectAsync(group.Host.Id).ConfigureAwait(false);
            var created = await client.CreateBotGroupThreadAsync(group.Id, cancellationToken).ConfigureAwait(false);
            return await UiAsync(() =>
            {
                var existing = group.Threads.FirstOrDefault(t => string.Equals(t.Id, created.Id, StringComparison.Ordinal));
                var thread = existing ?? new RemoteBotGroupThread(created.Id, created.Title);
                SyncBotGroupThreadFields(thread, created);
                if (existing is null)
                {
                    group.Threads.Add(thread);
                }
                group.Revision++;
                return thread;
            }).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    public async Task RespondBotGroupApprovalAsync(
        RemoteBotGroup group,
        string approvalId,
        string? optionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _connections.ClientOf(group.Host.Id) ?? await _connections.ConnectAsync(group.Host.Id).ConfigureAwait(false);
            await client.RespondBotGroupApprovalAsync(new BotGroupApprovalRespondMessage
            {
                ApprovalId = approvalId,
                OptionId = optionId,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    private void ApplyBotGroups(RemoteHost host, IReadOnlyList<BotGroupMessageDto> groupMessages)
    {
        var target = new List<RemoteBotGroup>(groupMessages.Count);
        foreach (var msg in groupMessages)
        {
            var group = host.BotGroups.FirstOrDefault(known => string.Equals(known.Id, msg.Id, StringComparison.Ordinal))
                ?? new RemoteBotGroup(host, msg.Id, msg.Name);
            SyncBotGroupFields(group, msg);
            target.Add(group);
        }
        Replace(host.BotGroups, target);
    }

    private static RemoteBotGroup UpsertBotGroup(RemoteHost host, BotGroupMessageDto msg)
    {
        var existing = host.BotGroups.FirstOrDefault(g => string.Equals(g.Id, msg.Id, StringComparison.Ordinal));
        var group = existing ?? new RemoteBotGroup(host, msg.Id, msg.Name);
        SyncBotGroupFields(group, msg);
        if (existing is null)
        {
            host.BotGroups.Insert(0, group);
        }
        return group;
    }

    private static void SyncBotGroupFields(RemoteBotGroup group, BotGroupMessageDto msg)
    {
        group.Name = msg.Name;
        group.Revision = msg.Revision;
        group.ReplyModeKind = string.IsNullOrWhiteSpace(msg.ReplyModeKind) ? "mentions" : msg.ReplyModeKind;
        group.ReplyModeBotId = msg.ReplyModeBotId;
        group.MaxHops = msg.MaxHops;
        group.IsUnread = msg.IsUnread;
        group.LatestAuthorName = msg.LatestAuthorName;
        group.LatestText = msg.LatestText;
        group.LatestAt = msg.LatestAt;
        group.PendingApprovalCount = msg.PendingApprovalCount;
        group.IsWorking = msg.IsWorking;
        group.CreatedAt = msg.CreatedAt;

        var members = msg.Members.Select(m =>
        {
            var member = group.Members.FirstOrDefault(known => string.Equals(known.BotId, m.BotId, StringComparison.Ordinal))
                ?? new RemoteBotGroupMember(m.BotId, m.ChatId, m.BotName);
            member.BotName = m.BotName;
            member.BotColor = string.IsNullOrWhiteSpace(m.BotColor) ? "#5B5BD6" : m.BotColor;
            member.AgentId = string.IsNullOrWhiteSpace(m.AgentId) ? "claude" : m.AgentId;
            member.IsPaused = m.IsPaused;
            return member;
        }).ToList();
        Replace(group.Members, members);

        var threads = msg.Threads.Select(t =>
        {
            var thread = group.Threads.FirstOrDefault(known => string.Equals(known.Id, t.Id, StringComparison.Ordinal))
                ?? new RemoteBotGroupThread(t.Id, t.Title);
            SyncBotGroupThreadFields(thread, t);
            return thread;
        }).ToList();
        Replace(group.Threads, threads);
    }

    private static void SyncBotGroupThreadFields(RemoteBotGroupThread thread, BotGroupThreadDto dto)
    {
        thread.Title = dto.Title;
        thread.CreatedAt = dto.CreatedAt;
        var members = dto.Members.Select(m =>
        {
            var member = thread.Members.FirstOrDefault(known => string.Equals(known.BotId, m.BotId, StringComparison.Ordinal))
                ?? new RemoteBotGroupMember(m.BotId, m.ChatId, m.BotName);
            member.BotName = m.BotName;
            member.BotColor = string.IsNullOrWhiteSpace(m.BotColor) ? "#5B5BD6" : m.BotColor;
            member.AgentId = string.IsNullOrWhiteSpace(m.AgentId) ? "claude" : m.AgentId;
            member.IsPaused = m.IsPaused;
            return member;
        }).ToList();
        Replace(thread.Members, members);
    }

    private static RemoteBotGroupEntry MapBotGroupEntry(BotGroupEntryDto dto) => new()
    {
        Id = dto.Id,
        GroupId = dto.GroupId,
        ThreadId = dto.ThreadId,
        Sequence = dto.Sequence,
        IsOwner = dto.IsOwner,
        AuthorBotId = dto.AuthorBotId,
        AuthorBotName = dto.AuthorBotName,
        Text = dto.Text,
        RequesterBotName = dto.RequesterBotName,
        IsMentionHandoff = dto.IsMentionHandoff,
        PausedByHopLimit = dto.PausedByHopLimit,
        HopDepth = dto.HopDepth,
        Turns = [.. dto.Turns.Select(t => new RemoteBotGroupTurn
        {
            OperationId = t.OperationId,
            BotId = t.BotId,
            BotName = t.BotName,
            BotColor = string.IsNullOrWhiteSpace(t.BotColor) ? "#5B5BD6" : t.BotColor,
            AgentId = string.IsNullOrWhiteSpace(t.AgentId) ? "claude" : t.AgentId,
            ChatId = t.ChatId,
            Status = string.IsNullOrWhiteSpace(t.Status) ? "queued" : t.Status,
            Reply = t.Reply,
            StreamingPreview = t.StreamingPreview,
            Error = t.Error,
            CreatedAt = t.CreatedAt,
            CompletedAt = t.CompletedAt,
        })],
        CreatedAt = dto.CreatedAt,
    };

    private static RemoteBotGroupApproval MapBotGroupApproval(BotGroupApprovalDto dto) => new()
    {
        ApprovalId = dto.ApprovalId,
        GroupId = dto.GroupId,
        ThreadId = dto.ThreadId,
        BotId = dto.BotId,
        BotName = dto.BotName,
        ChatId = dto.ChatId,
        Title = dto.Title,
        Detail = dto.Detail,
        WarningMessage = dto.WarningMessage,
        Options = [.. dto.Options.Select(o => new RemoteBotGroupApprovalOption(o.Id, o.Label, o.Detail))],
    };
}
