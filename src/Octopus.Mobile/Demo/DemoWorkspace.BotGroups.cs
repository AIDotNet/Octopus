using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Demo;

public sealed partial class DemoWorkspace
{
    private readonly Dictionary<string, List<RemoteBotGroupEntry>> _demoGroupEntries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RemoteBotGroupApproval>> _demoGroupApprovals = new(StringComparer.Ordinal);
    private long _demoGroupSeq = 10;

    public bool SupportsBotGroups(RemoteHost host) => host.IsPaired;

    private void SeedDemoBotGroups(RemoteHost mac, RemoteHost station)
    {
        _ = station;
        var now = DateTimeOffset.Now;
        var group = new RemoteBotGroup(mac, "bgroup-release", "发布协作组")
        {
            Revision = 1,
            ReplyModeKind = "mentions",
            MaxHops = 6,
            IsUnread = true,
            LatestAuthorName = "西瓜",
            LatestText = "PR #131 审查与构建均已通过，待你批准运行发布脚本。",
            LatestAt = now.AddMinutes(-3),
            PendingApprovalCount = 1,
            IsWorking = false,
            CreatedAt = now.AddHours(-6),
        };

        var xiguaMember = new RemoteBotGroupMember("bot-xigua", "bgchat-xigua-main", "西瓜")
        {
            BotColor = "#3B82D6",
            AgentId = "claude",
        };
        var reviewerMember = new RemoteBotGroupMember("bot-demo-reviewer", "bgchat-rev-main", "Codex 审查")
        {
            BotColor = "#1F9D74",
            AgentId = "codex",
        };
        group.Members.Add(xiguaMember);
        group.Members.Add(reviewerMember);

        var thread = new RemoteBotGroupThread("bthread-regression", "发布回归排查")
        {
            CreatedAt = now.AddHours(-2),
        };
        thread.Members.Add(new RemoteBotGroupMember("bot-xigua", "bgchat-xigua-t1", "西瓜")
        {
            BotColor = "#3B82D6",
            AgentId = "claude",
        });
        thread.Members.Add(new RemoteBotGroupMember("bot-demo-reviewer", "bgchat-rev-t1", "Codex 审查")
        {
            BotColor = "#1F9D74",
            AgentId = "codex",
        });
        group.Threads.Add(thread);

        mac.BotGroups.Add(group);

        _demoGroupEntries[RoomKey(group.Id, null)] =
        [
            new RemoteBotGroupEntry
            {
                Id = "bgmsg-1",
                GroupId = group.Id,
                ThreadId = null,
                Sequence = 1,
                IsOwner = true,
                Text = "请 @西瓜 和 @Codex 审查 一起核对 v1.2 发布前检查清单。",
                CreatedAt = now.AddMinutes(-15),
                Turns =
                [
                    new RemoteBotGroupTurn
                    {
                        OperationId = "bgop-1",
                        BotId = xiguaMember.BotId,
                        BotName = xiguaMember.BotName,
                        BotColor = xiguaMember.BotColor,
                        AgentId = xiguaMember.AgentId,
                        ChatId = xiguaMember.ChatId,
                        Status = "completed",
                        Reply = "CI 与签名清单已全部核对通过。@Codex 审查 帮我复核一下增量迁移脚本。",
                        CreatedAt = now.AddMinutes(-14),
                        CompletedAt = now.AddMinutes(-13),
                    },
                    new RemoteBotGroupTurn
                    {
                        OperationId = "bgop-2",
                        BotId = reviewerMember.BotId,
                        BotName = reviewerMember.BotName,
                        BotColor = reviewerMember.BotColor,
                        AgentId = reviewerMember.AgentId,
                        ChatId = reviewerMember.ChatId,
                        Status = "completed",
                        Reply = "已复核迁移脚本与回滚 SQL，无破坏性变更。",
                        CreatedAt = now.AddMinutes(-14),
                        CompletedAt = now.AddMinutes(-12),
                    },
                ],
            },
            new RemoteBotGroupEntry
            {
                Id = "bgmsg-2",
                GroupId = group.Id,
                ThreadId = null,
                Sequence = 2,
                IsOwner = false,
                AuthorBotId = xiguaMember.BotId,
                AuthorBotName = xiguaMember.BotName,
                RequesterBotName = xiguaMember.BotName,
                IsMentionHandoff = true,
                HopDepth = 1,
                Text = "自动接力：@西瓜 → @Codex 审查",
                CreatedAt = now.AddMinutes(-11),
                Turns =
                [
                    new RemoteBotGroupTurn
                    {
                        OperationId = "bgop-3",
                        BotId = reviewerMember.BotId,
                        BotName = reviewerMember.BotName,
                        BotColor = reviewerMember.BotColor,
                        AgentId = reviewerMember.AgentId,
                        ChatId = reviewerMember.ChatId,
                        Status = "completed",
                        Reply = "增量迁移压测通过，建议可以进入打标发布阶段。",
                        CreatedAt = now.AddMinutes(-11),
                        CompletedAt = now.AddMinutes(-9),
                    },
                ],
            },
            new RemoteBotGroupEntry
            {
                Id = "bgmsg-3",
                GroupId = group.Id,
                ThreadId = null,
                Sequence = 3,
                IsOwner = false,
                PausedByHopLimit = true,
                HopDepth = 6,
                Text = "Bot 间自动接力已达到上限（6 轮），点击继续可再放行 3 轮。",
                CreatedAt = now.AddMinutes(-4),
                Turns = [],
            },
        ];

        _demoGroupEntries[RoomKey(group.Id, thread.Id)] =
        [
            new RemoteBotGroupEntry
            {
                Id = "bgmsg-t1",
                GroupId = group.Id,
                ThreadId = thread.Id,
                Sequence = 4,
                IsOwner = true,
                Text = "追踪 iOS 弱网重连时的会话恢复耗时。",
                CreatedAt = now.AddHours(-1),
                Turns =
                [
                    new RemoteBotGroupTurn
                    {
                        OperationId = "bgop-t1",
                        BotId = xiguaMember.BotId,
                        BotName = xiguaMember.BotName,
                        BotColor = xiguaMember.BotColor,
                        AgentId = xiguaMember.AgentId,
                        ChatId = "bgchat-xigua-t1",
                        Status = "completed",
                        Reply = "已采样 20 次切网重连，平均恢复耗时 420ms。",
                        CreatedAt = now.AddMinutes(-55),
                        CompletedAt = now.AddMinutes(-53),
                    },
                ],
            },
        ];

        _demoGroupApprovals[group.Id] =
        [
            new RemoteBotGroupApproval
            {
                ApprovalId = "bgapp-1",
                GroupId = group.Id,
                ThreadId = null,
                BotId = xiguaMember.BotId,
                BotName = xiguaMember.BotName,
                ChatId = xiguaMember.ChatId,
                Title = "执行发布验证脚本",
                Detail = "dotnet test --configuration Release",
                WarningMessage = "将触发本地完整构建与签名校验",
                Options =
                [
                    new RemoteBotGroupApprovalOption("allow_once", "允许一次", "allow_once"),
                    new RemoteBotGroupApprovalOption("reject_once", "暂不执行", "reject_once"),
                ],
            },
        ];
    }

    public Task<RemoteBotGroup> CreateBotGroupAsync(RemoteHost host, CreateRemoteBotGroupRequest request, CancellationToken cancellationToken = default)
    {
        var name = string.IsNullOrWhiteSpace(request.Name) ? "新 Bot 群组" : request.Name.Trim();
        var id = "bgroup-" + _nextId++;
        var group = new RemoteBotGroup(host, id, name)
        {
            Revision = 1,
            ReplyModeKind = string.IsNullOrWhiteSpace(request.ReplyModeKind) ? "mentions" : request.ReplyModeKind,
            ReplyModeBotId = request.ReplyModeBotId,
            MaxHops = 6,
            CreatedAt = DateTimeOffset.Now,
        };

        var botIds = request.BotIds.Count > 0
            ? request.BotIds
            : host.Bots.Select(b => b.Id).Take(1).ToList();
        foreach (var botId in botIds.Distinct(StringComparer.Ordinal))
        {
            var bot = host.Bots.FirstOrDefault(b => string.Equals(b.Id, botId, StringComparison.Ordinal));
            var member = new RemoteBotGroupMember(botId, $"bgchat-{_nextId++}", bot?.Name ?? botId)
            {
                BotColor = bot?.Color ?? "#5B5BD6",
                AgentId = bot?.AgentId ?? "claude",
                IsPaused = bot?.IsPaused ?? false,
            };
            group.Members.Add(member);
        }

        if (string.Equals(group.ReplyModeKind, "bot", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(group.ReplyModeBotId))
        {
            group.ReplyModeBotId = group.Members.FirstOrDefault()?.BotId;
        }

        _demoGroupEntries[RoomKey(group.Id, null)] = [];
        _demoGroupApprovals[group.Id] = [];
        host.BotGroups.Insert(0, group);
        return Task.FromResult(group);
    }

    public Task<RemoteBotGroup> UpdateBotGroupAsync(RemoteBotGroup group, UpdateRemoteBotGroupRequest request, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            group.Name = request.Name.Trim();
        }
        if (request.BotIds is not null)
        {
            var updatedMembers = new List<RemoteBotGroupMember>();
            foreach (var botId in request.BotIds.Distinct(StringComparer.Ordinal))
            {
                var existing = group.Members.FirstOrDefault(m => string.Equals(m.BotId, botId, StringComparison.Ordinal));
                if (existing is not null)
                {
                    updatedMembers.Add(existing);
                }
                else
                {
                    var bot = group.Host.Bots.FirstOrDefault(b => string.Equals(b.Id, botId, StringComparison.Ordinal));
                    updatedMembers.Add(new RemoteBotGroupMember(botId, $"bgchat-{_nextId++}", bot?.Name ?? botId)
                    {
                        BotColor = bot?.Color ?? "#5B5BD6",
                        AgentId = bot?.AgentId ?? "claude",
                        IsPaused = bot?.IsPaused ?? false,
                    });
                }
            }
            if (updatedMembers.Count > 0)
            {
                group.Members.Clear();
                foreach (var member in updatedMembers)
                {
                    group.Members.Add(member);
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(request.ReplyModeKind))
        {
            group.ReplyModeKind = request.ReplyModeKind;
            group.ReplyModeBotId = request.ReplyModeBotId;
            if (string.Equals(group.ReplyModeKind, "bot", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(group.ReplyModeBotId))
            {
                group.ReplyModeBotId = group.Members.FirstOrDefault()?.BotId;
            }
        }
        if (request.MaxHops.HasValue)
        {
            group.MaxHops = Math.Clamp(request.MaxHops.Value, 1, 30);
        }
        group.Revision++;
        return Task.FromResult(group);
    }

    public Task DeleteBotGroupAsync(RemoteBotGroup group, CancellationToken cancellationToken = default)
    {
        group.Host.BotGroups.Remove(group);
        _demoGroupApprovals.Remove(group.Id);
        return Task.CompletedTask;
    }

    public Task<RemoteBotGroupRoomSnapshot> ReadBotGroupRoomAsync(
        RemoteBotGroup group,
        string? threadId = null,
        long? beforeSequence = null,
        int limit = 60,
        CancellationToken cancellationToken = default)
    {
        var key = RoomKey(group.Id, threadId);
        var list = _demoGroupEntries.GetValueOrDefault(key) ?? [];
        var filtered = beforeSequence.HasValue
            ? list.Where(e => e.Sequence < beforeSequence.Value).ToList()
            : list.ToList();
        var hasEarlier = filtered.Count > limit;
        var page = hasEarlier ? filtered.Skip(filtered.Count - limit).ToList() : filtered;

        var approvals = (_demoGroupApprovals.GetValueOrDefault(group.Id) ?? [])
            .Where(a => string.Equals(a.ThreadId ?? "", threadId ?? "", StringComparison.Ordinal))
            .ToList();

        group.IsUnread = false;
        return Task.FromResult(new RemoteBotGroupRoomSnapshot(group, threadId, page, approvals, hasEarlier));
    }

    public Task<RemoteBotGroupEntry> SendBotGroupMessageAsync(
        RemoteBotGroup group,
        string text,
        string? threadId = null,
        string? messageId = null,
        CancellationToken cancellationToken = default)
    {
        var key = RoomKey(group.Id, threadId);
        if (!_demoGroupEntries.TryGetValue(key, out var list))
        {
            list = [];
            _demoGroupEntries[key] = list;
        }

        var trimmed = text.Trim();
        var targets = ResolveDemoTargets(group, trimmed);
        var now = DateTimeOffset.Now;
        var turns = targets.Select(m => new RemoteBotGroupTurn
        {
            OperationId = "bgop-" + _nextId++,
            BotId = m.BotId,
            BotName = m.BotName,
            BotColor = m.BotColor,
            AgentId = m.AgentId,
            ChatId = m.ChatId,
            Status = "completed",
            Reply = $"{m.BotName} 已收到：「{trimmed}」，正在按群组上下文协作处理。",
            CreatedAt = now,
            CompletedAt = now,
        }).ToList();

        var entry = new RemoteBotGroupEntry
        {
            Id = string.IsNullOrWhiteSpace(messageId) ? "bgmsg-" + _nextId++ : messageId,
            GroupId = group.Id,
            ThreadId = threadId,
            Sequence = ++_demoGroupSeq,
            IsOwner = true,
            Text = trimmed,
            Turns = turns,
            CreatedAt = now,
        };
        list.Add(entry);

        if (!string.IsNullOrWhiteSpace(threadId))
        {
            var thread = group.Threads.FirstOrDefault(t => string.Equals(t.Id, threadId, StringComparison.Ordinal));
            if (thread is not null && string.IsNullOrWhiteSpace(thread.Title))
            {
                thread.Title = trimmed.Length > 40 ? trimmed[..40] : trimmed;
            }
        }

        group.LatestAuthorName = turns.LastOrDefault()?.BotName;
        group.LatestText = turns.LastOrDefault()?.Reply ?? trimmed;
        group.LatestAt = now;
        return Task.FromResult(entry);
    }

    public Task StopBotGroupAsync(
        RemoteBotGroup group,
        string? threadId = null,
        string? botId = null,
        CancellationToken cancellationToken = default)
    {
        var key = RoomKey(group.Id, threadId);
        if (_demoGroupEntries.TryGetValue(key, out var list))
        {
            for (var i = 0; i < list.Count; i++)
            {
                var entry = list[i];
                if (!entry.Turns.Any(t => t.IsActive && (botId is null || string.Equals(t.BotId, botId, StringComparison.Ordinal))))
                {
                    continue;
                }
                var updatedTurns = entry.Turns.Select(t =>
                    t.IsActive && (botId is null || string.Equals(t.BotId, botId, StringComparison.Ordinal))
                        ? new RemoteBotGroupTurn
                        {
                            OperationId = t.OperationId,
                            BotId = t.BotId,
                            BotName = t.BotName,
                            BotColor = t.BotColor,
                            AgentId = t.AgentId,
                            ChatId = t.ChatId,
                            Status = "stopped",
                            Reply = t.Reply,
                            Error = t.Error,
                            CreatedAt = t.CreatedAt,
                            CompletedAt = DateTimeOffset.Now,
                        }
                        : t).ToList();
                list[i] = new RemoteBotGroupEntry
                {
                    Id = entry.Id,
                    GroupId = entry.GroupId,
                    ThreadId = entry.ThreadId,
                    Sequence = entry.Sequence,
                    IsOwner = entry.IsOwner,
                    AuthorBotId = entry.AuthorBotId,
                    AuthorBotName = entry.AuthorBotName,
                    Text = entry.Text,
                    RequesterBotName = entry.RequesterBotName,
                    IsMentionHandoff = entry.IsMentionHandoff,
                    PausedByHopLimit = entry.PausedByHopLimit,
                    HopDepth = entry.HopDepth,
                    Turns = updatedTurns,
                    CreatedAt = entry.CreatedAt,
                };
            }
        }
        group.IsWorking = false;
        return Task.CompletedTask;
    }

    public Task ContinueBotGroupHandoffAsync(
        RemoteBotGroup group,
        string messageId,
        int extraHops = 3,
        CancellationToken cancellationToken = default)
    {
        foreach (var pair in _demoGroupEntries)
        {
            if (!pair.Key.StartsWith(group.Id + "|", StringComparison.Ordinal))
            {
                continue;
            }
            var idx = pair.Value.FindIndex(e => string.Equals(e.Id, messageId, StringComparison.Ordinal));
            if (idx < 0)
            {
                continue;
            }
            var paused = pair.Value[idx];
            var target = group.Members.FirstOrDefault();
            var now = DateTimeOffset.Now;
            pair.Value[idx] = new RemoteBotGroupEntry
            {
                Id = paused.Id,
                GroupId = paused.GroupId,
                ThreadId = paused.ThreadId,
                Sequence = paused.Sequence,
                IsOwner = false,
                PausedByHopLimit = false,
                IsMentionHandoff = true,
                HopDepth = paused.HopDepth,
                Text = $"已继续放行 {extraHops} 轮自动接力",
                Turns = target is null
                    ? []
                    :
                    [
                        new RemoteBotGroupTurn
                        {
                            OperationId = "bgop-" + _nextId++,
                            BotId = target.BotId,
                            BotName = target.BotName,
                            BotColor = target.BotColor,
                            AgentId = target.AgentId,
                            ChatId = target.ChatId,
                            Status = "completed",
                            Reply = $"{target.BotName} 已继续完成剩余协作步骤。",
                            CreatedAt = now,
                            CompletedAt = now,
                        },
                    ],
                CreatedAt = paused.CreatedAt,
            };
            break;
        }
        return Task.CompletedTask;
    }

    public Task<RemoteBotGroupThread> CreateBotGroupThreadAsync(RemoteBotGroup group, CancellationToken cancellationToken = default)
    {
        var threadId = "bthread-" + _nextId++;
        var thread = new RemoteBotGroupThread(threadId, null)
        {
            CreatedAt = DateTimeOffset.Now,
        };
        foreach (var m in group.Members)
        {
            thread.Members.Add(new RemoteBotGroupMember(m.BotId, $"bgchat-{_nextId++}", m.BotName)
            {
                BotColor = m.BotColor,
                AgentId = m.AgentId,
                IsPaused = m.IsPaused,
            });
        }
        group.Threads.Add(thread);
        group.Revision++;
        _demoGroupEntries[RoomKey(group.Id, threadId)] = [];
        return Task.FromResult(thread);
    }

    public Task RespondBotGroupApprovalAsync(
        RemoteBotGroup group,
        string approvalId,
        string? optionId,
        CancellationToken cancellationToken = default)
    {
        if (_demoGroupApprovals.TryGetValue(group.Id, out var list))
        {
            list.RemoveAll(a => string.Equals(a.ApprovalId, approvalId, StringComparison.Ordinal));
            group.PendingApprovalCount = list.Count;
        }
        else
        {
            group.PendingApprovalCount = 0;
        }
        return Task.CompletedTask;
    }

    private static List<RemoteBotGroupMember> ResolveDemoTargets(RemoteBotGroup group, string text)
    {
        var mentioned = group.Members
            .Where(m => text.Contains("@" + m.BotName, StringComparison.OrdinalIgnoreCase)
                || text.Contains("@" + m.BotId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (mentioned.Count > 0)
        {
            return mentioned;
        }
        if (string.Equals(group.ReplyModeKind, "all", StringComparison.OrdinalIgnoreCase))
        {
            return [.. group.Members];
        }
        if (string.Equals(group.ReplyModeKind, "bot", StringComparison.OrdinalIgnoreCase))
        {
            var chosen = group.Members.FirstOrDefault(m => string.Equals(m.BotId, group.ReplyModeBotId, StringComparison.Ordinal))
                ?? group.Members.FirstOrDefault();
            return chosen is null ? [] : [chosen];
        }
        return [];
    }

    private static string RoomKey(string groupId, string? threadId) => $"{groupId}|{threadId ?? ""}";
}
