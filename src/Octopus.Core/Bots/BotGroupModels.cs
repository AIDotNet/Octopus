namespace Octopus.Core.Bots;

public enum BotGroupReplyModeKind
{
    Bot,
    All,
    Mentions
}

public sealed class BotGroupReplyMode
{
    public BotGroupReplyModeKind Kind { get; set; } = BotGroupReplyModeKind.Mentions;
    /// <summary>Target bot id when <see cref="Kind"/> is <see cref="BotGroupReplyModeKind.Bot"/>.</summary>
    public string? BotId { get; set; }

    public BotGroupReplyMode Clone() => new() { Kind = Kind, BotId = BotId };
}

/// <summary>One local bot participating in a group room or thread. Rejoining gets a fresh <see cref="ChatId"/>.</summary>
public sealed class BotGroupMember
{
    public string BotId { get; set; } = "";
    /// <summary>Dedicated <see cref="BotChat.Id"/> (<see cref="BotChatKind.GroupMember"/>) for this room.</summary>
    public string ChatId { get; set; } = "";
    /// <summary>Stop watermark: queued turns and mention handoffs whose root sequence is &lt;= this value are aborted.</summary>
    public long StoppedThroughSequence { get; set; }
}

/// <summary>An additional topic room inside a <see cref="BotGroupDefinition"/>, with fresh member sessions.</summary>
public sealed class BotGroupThread
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Named from its first post (truncated to 80 chars) unless customized.</summary>
    public string? Title { get; set; }
    public List<BotGroupMember> Members { get; set; } = [];
    /// <summary>Sessions of bots that left this thread; preserved so history remains readable.</summary>
    public List<string> FormerChatIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Latest room message preview shown in the sidebar row.</summary>
public sealed class BotGroupLatestPreview
{
    /// <summary>Null when the owner posted it; otherwise the bot's display name.</summary>
    public string? AuthorName { get; set; }
    public string Text { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A multi-bot collaborative Group Room with its own transcript, reply mode, threads, and member sessions.</summary>
public sealed class BotGroupDefinition
{
    public const int DefaultMaxHops = 6;
    public const int MaxMembers = 32;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string HostDeviceKey { get; set; } = "local";
    /// <summary>Optimistic concurrency revision incremented on every membership or reply-mode update.</summary>
    public int Revision { get; set; } = 1;
    public BotGroupReplyMode ReplyMode { get; set; } = new();
    /// <summary>Maximum automatic bot-to-bot @mention handoff hops per owner request before pausing for confirmation.</summary>
    public int MaxHops { get; set; } = DefaultMaxHops;
    public List<BotGroupMember> Members { get; set; } = [];
    public List<string> FormerChatIds { get; set; } = [];
    public List<BotGroupThread> Threads { get; set; } = [];
    public bool IsExpanded { get; set; } = true;
    public bool IsUnread { get; set; }
    public BotGroupLatestPreview? Latest { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<BotGroupMember> GetRoomMembers(string? threadId) =>
        string.IsNullOrWhiteSpace(threadId)
            ? Members
            : Threads.FirstOrDefault(t => t.Id == threadId)?.Members ?? Members;

    public List<string> GetRoomFormerChatIds(string? threadId) =>
        string.IsNullOrWhiteSpace(threadId)
            ? FormerChatIds
            : Threads.FirstOrDefault(t => t.Id == threadId)?.FormerChatIds ?? FormerChatIds;
}

public enum BotGroupTurnStatus
{
    Queued,
    Running,
    Completed,
    Stopped,
    Failed
}

/// <summary>One bot turn triggered by a <see cref="BotGroupMessage"/>.</summary>
public sealed class BotGroupTurn
{
    public string OperationId { get; set; } = Guid.NewGuid().ToString("N");
    public string BotId { get; set; } = "";
    public string BotName { get; set; } = "";
    public string BotColor { get; set; } = "#5b5bd6";
    public string? BotIcon { get; set; }
    public string AgentId { get; set; } = "claude";
    public string ChatId { get; set; } = "";
    public BotGroupTurnStatus Status { get; set; } = BotGroupTurnStatus.Queued;
    /// <summary>Final reply saved when the turn completed (up to 16,000 chars); null while running or when empty.</summary>
    public string? Reply { get; set; }
    public string? Error { get; set; }
    /// <summary>Member bot ids woken via @mention handoff from this turn. Non-null means settled (never re-fired on replay).</summary>
    public List<string>? HandedOff { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>One entry in a Bot Group room or thread transcript.</summary>
public sealed class BotGroupMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GroupId { get; set; } = "";
    /// <summary>Null for the group's main room; otherwise the thread id.</summary>
    public string? ThreadId { get; set; }
    public long Sequence { get; set; }
    /// <summary>True for an owner post; false for a bot handoff line or hop-limit pause notice.</summary>
    public bool IsOwner { get; set; } = true;
    public string? AuthorBotId { get; set; }
    public string? AuthorBotName { get; set; }
    public string Text { get; set; } = "";
    /// <summary>Original owner post id that started this handoff chain.</summary>
    public string? RootRequestId { get; set; }
    /// <summary>Original owner post sequence that started this handoff chain.</summary>
    public long? RootSequence { get; set; }
    /// <summary>0 for an owner post; incremented by 1 on each bot-to-bot @mention handoff.</summary>
    public int HopDepth { get; set; }
    /// <summary>The bot whose reply triggered this handoff line.</summary>
    public string? RequesterBotId { get; set; }
    public string? RequesterBotName { get; set; }
    public bool IsMentionHandoff { get; set; }
    /// <summary>True when automatic handoff stopped because <see cref="HopDepth"/> reached the group's hop budget.</summary>
    public bool PausedByHopLimit { get; set; }
    /// <summary>Pending targets held when <see cref="PausedByHopLimit"/> is true, so the user can resume them.</summary>
    public List<string>? PausedTargetBotIds { get; set; }
    /// <summary>Source turn operation id that hit the hop limit, used to resume handoffs cleanly.</summary>
    public string? PausedSourceOperationId { get; set; }
    public List<BotGroupTurn> Turns { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public long EffectiveRootSequence => RootSequence ?? Sequence;
    public string EffectiveRootRequestId => string.IsNullOrWhiteSpace(RootRequestId) ? Id : RootRequestId!;
}

/// <summary>Persisted pending delivery entry written atomically with a reserved turn and cleared after admission.</summary>
public sealed class BotGroupPendingDelivery
{
    public string OperationId { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string? ThreadId { get; set; }
    public string ContextJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Persisted index of all Bot Groups in <c>bot-groups.json</c>.</summary>
public sealed class BotGroupsDocument
{
    public int Version { get; set; } = 1;
    public List<BotGroupDefinition> Groups { get; set; } = [];
}

/// <summary>Persisted transcript and pending deliveries for one group in <c>bot-groups/&lt;groupId&gt;.json</c>.</summary>
public sealed class BotGroupRoomDocument
{
    public int Version { get; set; } = 1;
    public string GroupId { get; set; } = "";
    public long NextSequence { get; set; } = 1;
    public List<BotGroupMessage> Messages { get; set; } = [];
    public List<BotGroupPendingDelivery> PendingDeliveries { get; set; } = [];
}
