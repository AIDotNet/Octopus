using System.Text.Json.Nodes;
using Octopus.Core.Agents;

namespace Octopus.App.Collab;

public enum CollabTaskStatus
{
    Queued,
    Running,
    WaitingApproval,
    Completed,
    Failed,
    Cancelled,
}

public sealed class CollabTaskRecord
{
    public required string TaskId { get; init; }
    public required string CallerItemId { get; init; }
    public required string TargetItemId { get; init; }
    public required string TargetAgentId { get; init; }
    public required string Title { get; init; }
    public required string Goal { get; init; }
    public required CollabIsolationMode Isolation { get; init; }
    public string? WorktreeBranch { get; init; }
    public string? WorktreePath { get; init; }
    public string? ThreadId { get; init; }
    public CollabTaskStatus Status { get; set; } = CollabTaskStatus.Running;
    public string? LatestActivity { get; set; }
    public string? ResultSummary { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class CollabMessageRecord
{
    public required string MessageId { get; init; }
    public required string ThreadId { get; init; }
    public required string FromItemId { get; init; }
    public required string FromLabel { get; init; }
    public string? FromAgentId { get; init; }
    public string? ToItemId { get; init; }
    public string? ToLabel { get; init; }
    public int Round { get; init; }
    public required string Text { get; init; }
    public string Status { get; init; } = "continue";
    public bool IsUserInterjection { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class CollabThreadRecord
{
    public required string ThreadId { get; init; }
    public required string Title { get; set; }
    public required string InitiatorItemId { get; init; }
    public List<string> ParticipantItemIds { get; init; } = [];
    public int Hops { get; set; }
    public int MaxHops { get; set; } = CollabSettings.DefaultMaxNegotiationHops;
    public string Status { get; set; } = "active";
    public List<CollabMessageRecord> Messages { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class CollabBubbledApproval
{
    private readonly Action<bool> _respond;
    private int _resolved;

    public CollabBubbledApproval(
        string approvalId,
        string parentItemId,
        string childItemId,
        string childAgentId,
        string childTitle,
        string toolTitle,
        string? detail,
        string? warningMessage,
        Action<bool> respond)
    {
        ApprovalId = approvalId;
        ParentItemId = parentItemId;
        ChildItemId = childItemId;
        ChildAgentId = childAgentId;
        ChildTitle = childTitle;
        ToolTitle = toolTitle;
        Detail = detail;
        WarningMessage = warningMessage;
        _respond = respond;
    }

    public string ApprovalId { get; }
    public string ParentItemId { get; }
    public string ChildItemId { get; }
    public string ChildAgentId { get; }
    public string ChildTitle { get; }
    public string ToolTitle { get; }
    public string? Detail { get; }
    public string? WarningMessage { get; }
    public bool IsResolved => Volatile.Read(ref _resolved) != 0;

    public bool TryRespond(bool allow)
    {
        if (Interlocked.Exchange(ref _resolved, 1) != 0) return false;
        _respond(allow);
        return true;
    }
}

/// <summary>Live collaboration handle implemented by an open ACP GUI tab.</summary>
public interface ICollabSessionBridge
{
    string ItemId { get; }
    string AgentId { get; }
    string DisplayTitle { get; }
    string WorkingDirectory { get; }
    bool IsBusy { get; }
    bool IsConnected { get; }
    string? CurrentStatusDetail { get; }

    /// <summary>Delivers a collaboration prompt or thread message into this session's prompt queue.</summary>
    Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default);

    /// <summary>Removes any still-queued (not yet dispatched) collaboration prompts matching the given thread or task.</summary>
    int CancelQueuedCollabPrompts(string? threadId = null, string? taskId = null) => 0;

    /// <summary>Produces a concise summary of recent conversation turns for peer inspection without waking the model.</summary>
    string ReadRecentSummary(int maxTurns = 5);
}

/// <summary>Dispatches authenticated MCP tool calls from <see cref="CollabMcpServer"/> into the coordinator.</summary>
public interface ICollabToolHandler
{
    Task<JsonObject> CallToolAsync(string callerItemId, string toolName, JsonObject? arguments, CancellationToken cancellationToken);
}

/// <summary>Read-only descriptor for one active peer session in the workspace.</summary>
public sealed record CollabPeerInfo(
    string ItemId,
    string AgentId,
    string AgentName,
    string Title,
    string Surface,
    string Status,
    string WorkingDirectory,
    string? Branch,
    string? ParentItemId,
    string? Isolation,
    string? Summary);

/// <summary>Read-only descriptor for one installed agent definition that can be spawned for delegation.</summary>
public sealed record CollabAvailableAgentInfo(
    string AgentId,
    string Name,
    bool SupportsAcp,
    string DefaultSurface,
    string[] Aliases);

/// <summary>Descriptor of a structured mention inserted in the composer.</summary>
public sealed record CollabMentionReference(
    string Kind,
    string TargetId,
    string DisplayLabel,
    string? AgentId = null,
    string? Branch = null);
