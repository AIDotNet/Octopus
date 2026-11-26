using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Octopus.Mobile.Workspace;

/// <summary>A multi-bot collaborative Group Room hosted on a paired <see cref="RemoteHost"/>.</summary>
public sealed partial class RemoteBotGroup : ObservableObject
{
    public RemoteBotGroup(RemoteHost host, string id, string name)
    {
        Host = host;
        Id = id;
        Name = name;
    }

    public RemoteHost Host { get; }
    public string Id { get; }

    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial int Revision { get; set; } = 1;
    /// <summary><c>bot</c>, <c>all</c>, or <c>mentions</c>.</summary>
    [ObservableProperty] public partial string ReplyModeKind { get; set; } = "mentions";
    [ObservableProperty] public partial string? ReplyModeBotId { get; set; }
    [ObservableProperty] public partial int MaxHops { get; set; } = 6;
    [ObservableProperty] public partial bool IsUnread { get; set; }
    [ObservableProperty] public partial string? LatestAuthorName { get; set; }
    [ObservableProperty] public partial string? LatestText { get; set; }
    [ObservableProperty] public partial DateTimeOffset? LatestAt { get; set; }
    [ObservableProperty] public partial int PendingApprovalCount { get; set; }
    [ObservableProperty] public partial bool IsWorking { get; set; }
    [ObservableProperty] public partial DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ObservableCollection<RemoteBotGroupMember> Members { get; } = [];
    public ObservableCollection<RemoteBotGroupThread> Threads { get; } = [];
}

/// <summary>One bot participating in a <see cref="RemoteBotGroup"/> or one of its threads.</summary>
public sealed partial class RemoteBotGroupMember : ObservableObject
{
    public RemoteBotGroupMember(string botId, string chatId, string botName)
    {
        BotId = botId;
        ChatId = chatId;
        BotName = botName;
    }

    public string BotId { get; }
    public string ChatId { get; }

    [ObservableProperty] public partial string BotName { get; set; }
    [ObservableProperty] public partial string BotColor { get; set; } = "#5B5BD6";
    [ObservableProperty] public partial string AgentId { get; set; } = "claude";
    [ObservableProperty] public partial bool IsPaused { get; set; }
}

/// <summary>A topic thread inside a <see cref="RemoteBotGroup"/> with its own isolated member sessions.</summary>
public sealed partial class RemoteBotGroupThread : ObservableObject
{
    public RemoteBotGroupThread(string id, string? title)
    {
        Id = id;
        Title = title;
    }

    public string Id { get; }

    [ObservableProperty] public partial string? Title { get; set; }
    [ObservableProperty] public partial DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ObservableCollection<RemoteBotGroupMember> Members { get; } = [];
}

/// <summary>One bot turn executed in response to a <see cref="RemoteBotGroupEntry"/>.</summary>
public sealed class RemoteBotGroupTurn
{
    public string OperationId { get; init; } = "";
    public string BotId { get; init; } = "";
    public string BotName { get; init; } = "";
    public string BotColor { get; init; } = "#5B5BD6";
    public string AgentId { get; init; } = "claude";
    public string ChatId { get; init; } = "";
    /// <summary><c>queued</c>, <c>running</c>, <c>completed</c>, <c>stopped</c>, or <c>failed</c>.</summary>
    public string Status { get; init; } = "queued";
    public string? Reply { get; init; }
    public string? StreamingPreview { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }

    public bool IsQueued => string.Equals(Status, "queued", StringComparison.OrdinalIgnoreCase);
    public bool IsRunning => string.Equals(Status, "running", StringComparison.OrdinalIgnoreCase);
    public bool IsCompleted => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase);
    public bool IsStopped => string.Equals(Status, "stopped", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => string.Equals(Status, "failed", StringComparison.OrdinalIgnoreCase);
    public bool IsActive => IsQueued || IsRunning;
}

/// <summary>One entry in a <see cref="RemoteBotGroup"/> room or thread transcript.</summary>
public sealed class RemoteBotGroupEntry
{
    public string Id { get; init; } = "";
    public string GroupId { get; init; } = "";
    public string? ThreadId { get; init; }
    public long Sequence { get; init; }
    public bool IsOwner { get; init; } = true;
    public string? AuthorBotId { get; init; }
    public string? AuthorBotName { get; init; }
    public string Text { get; init; } = "";
    public string? RequesterBotName { get; init; }
    public bool IsMentionHandoff { get; init; }
    public bool PausedByHopLimit { get; init; }
    public int HopDepth { get; init; }
    public IReadOnlyList<RemoteBotGroupTurn> Turns { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>One selectable button on a <see cref="RemoteBotGroupApproval"/>.</summary>
public sealed record RemoteBotGroupApprovalOption(string Id, string Label, string? Kind = null);

/// <summary>A pending permission or approval gate waiting inside a <see cref="RemoteBotGroup"/> room.</summary>
public sealed class RemoteBotGroupApproval
{
    public string ApprovalId { get; init; } = "";
    public string GroupId { get; init; } = "";
    public string? ThreadId { get; init; }
    public string BotId { get; init; } = "";
    public string BotName { get; init; } = "";
    public string ChatId { get; init; } = "";
    public string Title { get; init; } = "";
    public string? Detail { get; init; }
    public string? WarningMessage { get; init; }
    public IReadOnlyList<RemoteBotGroupApprovalOption> Options { get; init; } = [];
}

/// <summary>Snapshot of a <see cref="RemoteBotGroup"/> main room or thread.</summary>
public sealed record RemoteBotGroupRoomSnapshot(
    RemoteBotGroup Group,
    string? ThreadId,
    IReadOnlyList<RemoteBotGroupEntry> Messages,
    IReadOnlyList<RemoteBotGroupApproval> Approvals,
    bool HasEarlier);

public sealed record CreateRemoteBotGroupRequest(
    string Name,
    IReadOnlyList<string> BotIds,
    string? ReplyModeKind = null,
    string? ReplyModeBotId = null);

public sealed record UpdateRemoteBotGroupRequest(
    string GroupId,
    int Revision,
    string? Name = null,
    IReadOnlyList<string>? BotIds = null,
    string? ReplyModeKind = null,
    string? ReplyModeBotId = null,
    int? MaxHops = null);
