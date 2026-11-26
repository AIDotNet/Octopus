namespace Octopus.Remote.Protocol;

/// <summary>Snapshot of one Bot Group on a host, including members, threads, reply mode, and latest preview.</summary>
public sealed class BotGroupMessageDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Revision { get; set; } = 1;
    /// <summary><c>bot</c>, <c>all</c>, or <c>mentions</c>.</summary>
    public string ReplyModeKind { get; set; } = "mentions";
    public string? ReplyModeBotId { get; set; }
    public int MaxHops { get; set; } = 6;
    public bool IsUnread { get; set; }
    public string? LatestAuthorName { get; set; }
    public string? LatestText { get; set; }
    public DateTimeOffset? LatestAt { get; set; }
    public int PendingApprovalCount { get; set; }
    public bool IsWorking { get; set; }
    public List<BotGroupMemberDto> Members { get; set; } = [];
    public List<BotGroupThreadDto> Threads { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BotGroupMemberDto
{
    public string BotId { get; set; } = "";
    public string ChatId { get; set; } = "";
    public string BotName { get; set; } = "";
    public string BotColor { get; set; } = "#5b5bd6";
    public string AgentId { get; set; } = "claude";
    public bool IsPaused { get; set; }
}

public sealed class BotGroupThreadDto
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public List<BotGroupMemberDto> Members { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BotGroupListMessage
{
    public List<BotGroupMessageDto> Groups { get; set; } = [];
}

public sealed class BotGroupTurnDto
{
    public string OperationId { get; set; } = "";
    public string BotId { get; set; } = "";
    public string BotName { get; set; } = "";
    public string BotColor { get; set; } = "#5b5bd6";
    public string AgentId { get; set; } = "claude";
    public string ChatId { get; set; } = "";
    /// <summary><c>queued</c>, <c>running</c>, <c>completed</c>, <c>stopped</c>, or <c>failed</c>.</summary>
    public string Status { get; set; } = "queued";
    public string? Reply { get; set; }
    public string? StreamingPreview { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class BotGroupEntryDto
{
    public string Id { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string? ThreadId { get; set; }
    public long Sequence { get; set; }
    public bool IsOwner { get; set; } = true;
    public string? AuthorBotId { get; set; }
    public string? AuthorBotName { get; set; }
    public string Text { get; set; } = "";
    public string? RequesterBotName { get; set; }
    public bool IsMentionHandoff { get; set; }
    public bool PausedByHopLimit { get; set; }
    public int HopDepth { get; set; }
    public List<BotGroupTurnDto> Turns { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BotGroupApprovalDto
{
    public string ApprovalId { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string? ThreadId { get; set; }
    public string BotId { get; set; } = "";
    public string BotName { get; set; } = "";
    public string ChatId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Detail { get; set; }
    public string? WarningMessage { get; set; }
    public List<AcpOptionMessage> Options { get; set; } = [];
}

public sealed class BotGroupRoomReadRequestMessage
{
    public string GroupId { get; set; } = "";
    public string? ThreadId { get; set; }
    public long? BeforeSequence { get; set; }
    public int Limit { get; set; } = 60;
}

public sealed class BotGroupRoomSnapshotMessage
{
    public BotGroupMessageDto Group { get; set; } = new();
    public string? ThreadId { get; set; }
    public List<BotGroupEntryDto> Messages { get; set; } = [];
    public List<BotGroupApprovalDto> Approvals { get; set; } = [];
    public bool HasEarlier { get; set; }
}

public sealed class CreateBotGroupMessage
{
    public string Name { get; set; } = "";
    public List<string> BotIds { get; set; } = [];
    public string? ReplyModeKind { get; set; }
    public string? ReplyModeBotId { get; set; }
}

public sealed class UpdateBotGroupMessage
{
    public string GroupId { get; set; } = "";
    public int Revision { get; set; }
    public string? Name { get; set; }
    public List<string>? BotIds { get; set; }
    public string? ReplyModeKind { get; set; }
    public string? ReplyModeBotId { get; set; }
    public int? MaxHops { get; set; }
}

public sealed class BotGroupRefMessage
{
    public string GroupId { get; set; } = "";
}

public sealed class BotGroupSendMessage
{
    public string GroupId { get; set; } = "";
    public string? ThreadId { get; set; }
    public int Revision { get; set; }
    public string MessageId { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class BotGroupStopMessage
{
    public string GroupId { get; set; } = "";
    public string? ThreadId { get; set; }
    public string? BotId { get; set; }
}

public sealed class BotGroupContinueHandoffMessage
{
    public string GroupId { get; set; } = "";
    public string MessageId { get; set; } = "";
    public int ExtraHops { get; set; } = 3;
}

public sealed class BotGroupApprovalRespondMessage
{
    public string ApprovalId { get; set; } = "";
    public string? OptionId { get; set; }
}
