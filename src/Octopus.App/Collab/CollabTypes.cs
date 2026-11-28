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

/// <summary>Where a negotiation thread stands. <see cref="CollabThreadStatusKeys"/> gives the wire / persisted spelling.</summary>
public enum CollabThreadStatus
{
    /// <summary>Open: a reply is expected or may follow.</summary>
    Active,
    /// <summary>Both sides consider it settled (also the outcome of an implicit end-of-turn report).</summary>
    Agreed,
    /// <summary>The responder cannot proceed.</summary>
    Blocked,
    /// <summary>The task behind it was cancelled, or a participant stopped or went away.</summary>
    Cancelled,
    /// <summary>Octopus restarted while it was open.</summary>
    Interrupted,
    /// <summary>Its hop budget is used up; the user can extend it.</summary>
    PausedBudget,
}

public static class CollabThreadStatusKeys
{
    /// <summary>The spelling used in tool results, the ledger file and the UI (<c>paused_budget</c>).</summary>
    public static string ToKey(this CollabThreadStatus status) => status switch
    {
        CollabThreadStatus.Agreed => "agreed",
        CollabThreadStatus.Blocked => "blocked",
        CollabThreadStatus.Cancelled => "cancelled",
        CollabThreadStatus.Interrupted => "interrupted",
        CollabThreadStatus.PausedBudget => "paused_budget",
        _ => "active",
    };

    public static CollabThreadStatus Parse(string? key) => key switch
    {
        "agreed" => CollabThreadStatus.Agreed,
        "blocked" => CollabThreadStatus.Blocked,
        "cancelled" => CollabThreadStatus.Cancelled,
        "interrupted" => CollabThreadStatus.Interrupted,
        "paused_budget" => CollabThreadStatus.PausedBudget,
        "active" => CollabThreadStatus.Active,
        _ => CollabThreadStatus.Interrupted,
    };
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

    /// <summary>Coordinator event sequence of the last change; lets <c>await_events</c> report each change exactly once.</summary>
    public long UpdatedSeq { get; set; }

    public bool IsFinished => Status is CollabTaskStatus.Completed or CollabTaskStatus.Failed or CollabTaskStatus.Cancelled;
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
    public CollabThreadStatus Status { get; set; } = CollabThreadStatus.Active;
    public string StatusKey => Status.ToKey();
    public List<CollabMessageRecord> Messages { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Coordinator event sequence of the last change; lets <c>await_events</c> report each change exactly once.</summary>
    public long UpdatedSeq { get; set; }

    /// <summary>True once the thread can no longer progress without a new message (agreed, blocked, cancelled, interrupted, out of budget).</summary>
    public bool IsClosed => Status != CollabThreadStatus.Active;
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

    /// <summary>True when this bridge tracks which turn is currently running (for example, a live ACP GUI tab).</summary>
    bool TracksActiveTurn => false;

    /// <summary>True when a bridge without active-turn tracking may safely infer delegation ownership from the head pending task.</summary>
    bool CanInferPendingOwnership => true;

    /// <summary>True while a turn is actively running on this bridge (when <see cref="TracksActiveTurn"/> is true).</summary>
    bool HasActiveTurn => false;

    /// <summary>The collaboration thread id of the currently active turn, or null when idle or running an ordinary user turn.</summary>
    string? ActiveCollabThreadId => null;

    /// <summary>The collaboration task id of the currently active turn, or null when idle or running an ordinary user turn.</summary>
    string? ActiveCollabTaskId => null;

    /// <summary>The collaboration delivery id of the currently active turn, or null when idle or running an ordinary user turn.</summary>
    string? ActiveCollabDeliveryId => null;

    /// <summary>Opaque per-turn token when this bridge is running an active collaboration turn whose parent task may live on another coordinator/host.</summary>
    string? ActiveExternalTurnKey => null;

    /// <summary>
    /// Reads the active collaboration turn identity for this session when supported by the bridge.
    /// Returns true when the bridge tracks active turns (even if currently idle, in which case all out values are null).
    /// </summary>
    bool TryGetActiveCollabTurn(out string? threadId, out string? taskId, out string? deliveryId)
    {
        if (TracksActiveTurn)
        {
            var active = HasActiveTurn;
            threadId = active ? ActiveCollabThreadId : null;
            taskId = active ? ActiveCollabTaskId : null;
            deliveryId = active ? ActiveCollabDeliveryId : null;
            return true;
        }
        threadId = null;
        taskId = null;
        deliveryId = null;
        return false;
    }

    /// <summary>Delivers a collaboration prompt or thread message into this session's prompt queue.</summary>
    Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default);

    /// <summary>Delivers a collaboration prompt or thread message while indicating whether this delivery expects a reply or implicit turn-end report.</summary>
    Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId, string? taskId, bool expectReply, CancellationToken cancellationToken)
        => DeliverCollabPromptAsync(promptText, threadId, taskId, cancellationToken);

    /// <summary>Delivers a collaboration prompt with an optional freshness check evaluated right before committing to the session.</summary>
    Task<bool> DeliverCollabPromptAsync(
        string promptText,
        string? threadId,
        string? taskId,
        bool expectReply,
        Func<bool>? shouldCommit,
        CancellationToken cancellationToken)
        => DeliverCollabPromptAsync(promptText, threadId, taskId, expectReply, deliveryId: null, shouldCommit, cancellationToken);

    Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId, string? taskId, bool expectReply, string? deliveryId, Func<bool>? shouldCommit, CancellationToken cancellationToken)
        => shouldCommit is not null && !shouldCommit()
            ? Task.FromResult(false)
            : DeliverCollabPromptAsync(promptText, threadId, taskId, expectReply, deliveryId, cancellationToken);

    /// <summary>Delivers a collaboration prompt or thread message with an optional per-delivery identifier shared with turn-end callbacks.</summary>
    Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId, string? taskId, bool expectReply, string? deliveryId, CancellationToken cancellationToken)
        => DeliverCollabPromptAsync(promptText, threadId, taskId, expectReply, cancellationToken);

    /// <summary>
    /// True when this bridge supports exact per-delivery queue withdrawal without falling back to broad
    /// thread- or task-wide cancellation.
    /// </summary>
    bool SupportsExactDeliveryWithdrawal => false;

    /// <summary>Removes any still-queued (not yet dispatched) collaboration prompts matching the given thread or task.</summary>
    int CancelQueuedCollabPrompts(string? threadId = null, string? taskId = null) => 0;

    /// <summary>
    /// Removes any still-queued (not yet dispatched) collaboration prompts matching the given thread, task, or delivery id.
    /// When <paramref name="deliveryId"/> is non-empty, bridges that only implement the two-argument overload do not fall
    /// back to broad thread- or task-wide deletion.
    /// </summary>
    int CancelQueuedCollabPrompts(string? threadId, string? taskId, string? deliveryId) =>
        string.IsNullOrWhiteSpace(deliveryId)
            ? CancelQueuedCollabPrompts(threadId, taskId)
            : 0;

    /// <summary>Stops the session's running turn, as if the user pressed Stop. Used when a delegated task is cancelled.</summary>
    Task CancelActiveTurnAsync() => Task.CompletedTask;

    /// <summary>Stops the session's running turn only if it matches the given collaboration thread or task (or unconditionally when neither is specified).</summary>
    Task CancelActiveTurnAsync(string? threadId, string? taskId) => CancelActiveTurnAsync();

    /// <summary>Stops the session's running turn only if it matches the given collaboration thread, task, or delivery id (or unconditionally when none is specified).</summary>
    Task CancelActiveTurnAsync(string? threadId, string? taskId, string? deliveryId) => CancelActiveTurnAsync(threadId, taskId);

    /// <summary>Produces a concise summary of recent conversation turns for peer inspection without waking the model.</summary>
    string ReadRecentSummary(int maxTurns = 5);
}

/// <summary>Dispatches authenticated MCP tool calls from <see cref="CollabMcpServer"/> into the coordinator.</summary>
public interface ICollabToolHandler
{
    Task<JsonObject> CallToolAsync(string callerItemId, string toolName, JsonObject? arguments, CancellationToken cancellationToken);

    /// <summary>The <c>tools/list</c> schemas; null lets the server use the tools it was created with.</summary>
    JsonArray? ListTools() => null;
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
    string? Summary,
    string? Host = null);

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
