using System.Text.Json;

using Octopus.Core.Agents;

namespace Octopus.App.Collab.Ledger;

/// <summary>A session owes <see cref="CallerItemId"/> an answer to <see cref="TaskId"/> / <see cref="ThreadId"/>.</summary>
internal sealed class PendingExpectation(
    string callerItemId,
    string? taskId,
    string? threadId,
    DateTimeOffset dispatchedAt,
    string? deliveryId = null,
    long? wakeupSeq = null,
    bool reopenCompletedTask = false,
    string? callerTerminalLinkId = null,
    bool transferCallerTerminalOwnership = false,
    int? expectedInitialTaskOwnershipEpoch = null,
    object? callerTerminalSession = null,
    long dispatchedAcceptedSeq = 0)
{
    public string CallerItemId { get; } = callerItemId;
    public string? TaskId { get; } = taskId;
    public string? ThreadId { get; } = threadId;
    public DateTimeOffset DispatchedAt { get; } = dispatchedAt;
    public string? DeliveryId { get; } = deliveryId;

    /// <summary>
    /// When this expectation came from a reply wakeup (<c>status: "continue"</c>), the ledger sequence of the reply
    /// that triggered it; null for direct delegations and <c>send_message</c>.
    /// </summary>
    public long? WakeupSeq { get; } = wakeupSeq;

    /// <summary>True when accepting this follow-up delivery should reopen an already-completed task on the same thread.</summary>
    public bool ReopenCompletedTask { get; } = reopenCompletedTask;

    /// <summary>Originating caller terminal link id captured at dispatch time for outbound ownership transfer at acceptance.</summary>
    public string? CallerTerminalLinkId { get; } = callerTerminalLinkId;

    /// <summary>True when accepting this delivery should bind or clear outbound caller terminal ownership on the target task(s).</summary>
    public bool TransferCallerTerminalOwnership { get; } = transferCallerTerminalOwnership;

    /// <summary>Initial task ownership epoch for a newly created delegation so a stale unaccepted receipt cannot overwrite an accepted successor.</summary>
    public int? ExpectedInitialTaskOwnershipEpoch { get; } = expectedInitialTaskOwnershipEpoch;

    /// <summary>Opaque caller terminal session instance captured at dispatch time to detect caller retirement before acceptance.</summary>
    public object? CallerTerminalSession { get; } = callerTerminalSession;

    /// <summary>Latest accepted delivery sequence observed on (target, thread) when this delivery was enqueued.</summary>
    public long DispatchedAcceptedSeq { get; } = dispatchedAcceptedSeq;

    /// <summary>
    /// True once an explicit <c>reply_thread</c> already settled this turn's expectation while later work was
    /// already queued behind it. Kept as a one-shot marker only so an uncorrelated <c>end_turn</c> from the same
    /// turn consumes this slot instead of blindly popping the next queued job.
    /// </summary>
    public bool SettledExplicitly { get; set; }

    /// <summary>
    /// True once the target's remote turn ended normally while unfinished host-side child delegations remain open.
    /// Retained so disconnect/error teardown still settles the parent task while later queued turns are not blocked.
    /// </summary>
    public bool Deferred { get; set; }

    /// <summary>True once the target bridge atomically claimed this delivery for active turn execution.</summary>
    public bool Dispatched { get; set; }

    /// <summary>True once this exact delivery was accepted by its target bridge (or idempotently on delivery return).</summary>
    public bool Accepted { get; set; }

    /// <summary>Monotonic acceptance order assigned when <see cref="Accepted"/> transitions to true.</summary>
    public long AcceptedSeq { get; set; }

    /// <summary>Thread turn revision assigned when this delivery was accepted; used to detect supersession.</summary>
    public long? AcceptedThreadRevision { get; set; }

    /// <summary>Opaque bridge-lifetime token that received this delivery; distinguishes replacement tabs from same-bridge FIFO queues.</summary>
    public object? BridgeOwner { get; set; }

    /// <summary>Primitive ingress token linking an ID-less legacy queued prompt back to this pending expectation after receipt.</summary>
    public long IngressToken { get; set; }

    /// <summary>
    /// Identifier of the specific terminal link that acknowledged writing this delivery to its PTY, or null before
    /// write acknowledgement (or for non-terminal bridges).
    /// </summary>
    public string? OwningTerminalLinkId { get; set; }
}

/// <summary>An active reply wakeup delivery tracked by its delivery id and triggering ledger sequence.</summary>
internal sealed class QueuedWakeup(
    string deliveryId,
    string threadId,
    long replySeq,
    IReadOnlyList<(string TaskId, long Seq)> matchingTasks,
    long sessionEpoch = 0)
{
    public string DeliveryId { get; } = deliveryId;
    public string ThreadId { get; } = threadId;
    public long ReplySeq { get; } = replySeq;
    public IReadOnlyList<(string TaskId, long Seq)> MatchingTasks { get; } = matchingTasks;
    public long SessionEpoch { get; } = sessionEpoch;

    /// <summary>True once the target bridge atomically claimed this wakeup for active turn execution.</summary>
    public bool Dispatched { get; set; }

    /// <summary>True when accepted by a bridge that manages per-delivery release callbacks.</summary>
    public bool ManagedLifetime { get; set; }

    /// <summary>True once this wakeup has been explicitly revoked (failed, consumed, cancelled, or released), distinct from legacy bookkeeping retirement.</summary>
    public bool Revoked { get; set; }
}

/// <summary>An await_events call blocked until something changes.</summary>
internal sealed record EventWaiter(
    string CallerItemId,
    HashSet<string> FilterIds,
    TaskCompletionSource<bool> Tcs,
    CancellationToken CancellationToken)
{
    public bool IsCancelled => CancellationToken.IsCancellationRequested;

    public bool Matches(string threadId, IReadOnlyCollection<string> taskIds) =>
        FilterIds.Count == 0 || FilterIds.Contains(threadId) || taskIds.Any(FilterIds.Contains);
}

/// <summary>
/// Everything the collaboration bus remembers: tasks, threads, open permission cards, who owes whom an answer, blocked
/// await_events calls and what each caller has already been told. One lock (<see cref="Gate"/>) guards all of it.
/// <para>
/// Every change of a task or thread goes through the transition methods (<see cref="SetTaskStatus"/>,
/// <see cref="SetThreadStatus"/>, <see cref="AppendMessage"/>, <see cref="Add(CollabTaskRecord)"/>…), which give it a new
/// sequence number (so await_events reports it once) and mark the ledger for saving. Call them while holding
/// <see cref="Gate"/>.
/// </para>
/// </summary>
internal sealed class CollabLedger : IDisposable
{
    private const int PersistDebounceMs = 400;
    private const int PersistRetryBaseMs = 400;
    private const int PersistRetryMaxMs = 3200;
    private const int MaxPersistRetries = 3;
    private const int MaxPersistedTasks = 500;
    private const int MaxPersistedThreads = 300;
    private const int MaxPersistedMessagesPerThread = 40;
    private static readonly TimeSpan PersistRetention = TimeSpan.FromDays(14);

    private static readonly CollabLedgerJsonContext Json = new(new JsonSerializerOptions
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private readonly CollabStateStore? _store;
    private readonly object _persistGate = new();
    private Timer? _persistTimer;
    private int _idSeq;
    private long _eventSeq;
    private long _acceptSeq;
    private long _dirtyVersion;
    private long _persistedVersion;
    private long _failedRetryVersion;
    private int _consecutivePersistFailures;
    private bool _disposed;

    public CollabLedger(CollabStateStore? store) => _store = store;

    public object Gate { get; } = new();
    public Dictionary<string, CollabTaskRecord> Tasks { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, CollabThreadRecord> Threads { get; } = new(StringComparer.Ordinal);
    /// <summary>Open permission cards raised by children, keyed by child + card: a child (a remote one especially) may have several.</summary>
    public Dictionary<(string Child, string Approval), CollabBubbledApproval> Approvals { get; } = [];
    /// <summary>Per session: the answers it owes in dispatch order (implicit replies when turns end without reply_thread).</summary>
    public Dictionary<string, List<PendingExpectation>> Pending { get; } = new(StringComparer.Ordinal);
    /// <summary>Per recipient: active reply wakeup deliveries tracked until consumed, cancelled, or settled. Hold <see cref="Gate"/>.</summary>
    public Dictionary<string, List<QueuedWakeup>> ActiveWakeups { get; } = new(StringComparer.Ordinal);
    /// <summary>In-memory child task → owning parent task links for nested delegations (hold <see cref="Gate"/>).</summary>
    private readonly Dictionary<string, string> _parentTaskByChildTask = new(StringComparer.Ordinal);
    /// <summary>In-memory child task → opaque external/remote turn key for host-side delegations owned by a remote turn (hold <see cref="Gate"/>).</summary>
    private readonly Dictionary<string, string> _externalTurnByChildTask = new(StringComparer.Ordinal);
    /// <summary>In-memory outbound task → originating caller terminal link id (hold <see cref="Gate"/>).</summary>
    private readonly Dictionary<string, string> _callerTerminalLinkByTask = new(StringComparer.Ordinal);
    /// <summary>Tasks cancelled specifically by retiring their caller's terminal session, mapped to the retired link id (empty if closed without a link id) so only a live replacement terminal of the same caller can reopen them at actual acceptance (hold <see cref="Gate"/>).</summary>
    private readonly Dictionary<string, string> _terminalCallerRetiredLinkByTask = new(StringComparer.Ordinal);
    /// <summary>Per-task ownership epoch incremented on finish/reopen/acceptance transitions, unaffected by approval/activity updates (hold <see cref="Gate"/>).</summary>
    private readonly Dictionary<string, int> _taskOwnershipEpoch = new(StringComparer.Ordinal);
    /// <summary>Per-task latest accepted owning delivery id (hold <see cref="Gate"/>).</summary>
    private readonly Dictionary<string, string> _taskOwningDelivery = new(StringComparer.Ordinal);
    /// <summary>Per-participant thread membership index kept synchronized with <see cref="Threads"/> (hold <see cref="Gate"/>).</summary>
    private readonly Dictionary<string, HashSet<string>> _threadsByParticipant = new(StringComparer.Ordinal);
    /// <summary>Per-child approval parent counts kept synchronized with <see cref="Approvals"/> (hold <see cref="Gate"/>).</summary>
    private readonly Dictionary<string, Dictionary<string, int>> _approvalParentsByChild = new(StringComparer.Ordinal);
    public List<EventWaiter> Waiters { get; } = [];
    /// <summary>Per caller: task/thread id → the sequence number await_events last reported.</summary>
    public Dictionary<string, Dictionary<string, long>> Seen { get; } = new(StringComparer.Ordinal);
    /// <summary>Per-thread revision incremented on non-implicit transitions (sends, explicit replies, aborts, cancellations). Hold <see cref="Gate"/>.</summary>
    private readonly Dictionary<string, long> _threadTurnRevision = new(StringComparer.Ordinal);
    /// <summary>Per-item session epoch incremented on full session unregister/drain to invalidate retired legacy wakeup guards (hold <see cref="Gate"/>).</summary>
    private readonly Dictionary<string, long> _sessionEpoch = new(StringComparer.Ordinal);
    /// <summary>Per-recipient read/snapshot wakeup consumption ceilings (thread/task id -> max observed seq) for invalidating retired legacy guards without altering await_events Seen (hold <see cref="Gate"/>).</summary>
    private readonly Dictionary<string, Dictionary<string, long>> _readObservedSeq = new(StringComparer.Ordinal);
    /// <summary>Highest accepted delivery sequence per (target item, thread), persisted across pending-to-publication intervals so superseded aborts cannot overwrite a newer accepted answer (hold <see cref="Gate"/>).</summary>
    private readonly Dictionary<(string TargetItemId, string ThreadId), long> _latestAcceptedSeqByTargetThread = new();
    /// <summary>Highest dispatched (or non-tracking bridge accepted) delivery sequence per (target item, thread), so stale completions cannot overwrite a newer executing/completed turn while queued FIFO turns behind an active turn still let the earlier turn publish (hold <see cref="Gate"/>).</summary>
    private readonly Dictionary<(string TargetItemId, string ThreadId), long> _latestDispatchedSeqByTargetThread = new();

    /// <summary>A new id: <c>prefix_n_xxxx</c>, n counting up across restarts.</summary>
    public string NextId(string prefix) => $"{prefix}_{Interlocked.Increment(ref _idSeq)}_{Guid.NewGuid().ToString("N")[..4]}";

    /// <summary>Records monotonic acceptance order for a delivery under <see cref="Gate"/> and updates the latest accepted sequence for the target/thread.</summary>
    public long RecordAcceptedDelivery(string targetItemId, string? threadId, bool markDispatched = false)
    {
        var seq = ++_acceptSeq;
        if (!string.IsNullOrWhiteSpace(targetItemId) && !string.IsNullOrWhiteSpace(threadId))
        {
            var key = (targetItemId, threadId);
            _latestAcceptedSeqByTargetThread[key] = seq;
            if (markDispatched)
            {
                _latestDispatchedSeqByTargetThread[key] = seq;
            }
        }
        return seq;
    }

    public void RecordDispatchedDelivery(string targetItemId, string? threadId, long acceptedSeq)
    {
        if (acceptedSeq <= 0 || string.IsNullOrWhiteSpace(targetItemId) || string.IsNullOrWhiteSpace(threadId)) return;
        var key = (targetItemId, threadId);
        if (acceptedSeq > _latestDispatchedSeqByTargetThread.GetValueOrDefault(key))
        {
            _latestDispatchedSeqByTargetThread[key] = acceptedSeq;
        }
    }

    /* ---------- transitions (hold Gate) ---------- */

    public void Add(CollabTaskRecord task)
    {
        Tasks[task.TaskId] = task;
        Touch(task);
    }

    public void Add(CollabThreadRecord thread)
    {
        if (Threads.TryGetValue(thread.ThreadId, out var existing))
        {
            UnindexThreadLocked(existing);
        }
        Threads[thread.ThreadId] = thread;
        IndexThreadLocked(thread);
        Touch(thread);
    }

    public void AddThreadParticipant(CollabThreadRecord thread, string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return;
        if (!thread.ParticipantItemIds.Contains(itemId, StringComparer.Ordinal))
        {
            thread.ParticipantItemIds.Add(itemId);
        }
        IndexParticipantLocked(itemId, thread.ThreadId);
    }

    public bool RemoveTask(string taskId)
    {
        if (!Tasks.Remove(taskId)) return false;
        _parentTaskByChildTask.Remove(taskId);
        _externalTurnByChildTask.Remove(taskId);
        _callerTerminalLinkByTask.Remove(taskId);
        _terminalCallerRetiredLinkByTask.Remove(taskId);
        _taskOwnershipEpoch.Remove(taskId);
        _taskOwningDelivery.Remove(taskId);
        _dirtyVersion++;
        return true;
    }

    public bool RemoveThread(string threadId)
    {
        if (!Threads.Remove(threadId, out var removed)) return false;
        UnindexThreadLocked(removed);
        _threadTurnRevision.Remove(threadId);
        List<(string TargetItemId, string ThreadId)>? acceptedKeys = null;
        foreach (var key in _latestAcceptedSeqByTargetThread.Keys)
        {
            if (string.Equals(key.ThreadId, threadId, StringComparison.Ordinal))
            {
                (acceptedKeys ??= []).Add(key);
            }
        }
        if (acceptedKeys is not null)
        {
            foreach (var key in acceptedKeys)
            {
                _latestAcceptedSeqByTargetThread.Remove(key);
                _latestDispatchedSeqByTargetThread.Remove(key);
            }
        }
        _dirtyVersion++;
        return true;
    }

    public void SetApproval(CollabBubbledApproval approval)
    {
        var key = (approval.ChildItemId, approval.ApprovalId);
        if (Approvals.TryGetValue(key, out var existing))
        {
            UnindexApprovalLocked(existing);
        }
        Approvals[key] = approval;
        IndexApprovalLocked(approval);
    }

    public (List<CollabBubbledApproval> Cleared, bool HasRemainingForChild) ClearApprovals(string childItemId, string? approvalId)
    {
        List<CollabBubbledApproval> cleared = [];
        List<(string Child, string Approval)> keys = [];
        foreach (var (key, value) in Approvals)
        {
            if (string.Equals(key.Child, childItemId, StringComparison.Ordinal)
                && (approvalId is null || string.Equals(key.Approval, approvalId, StringComparison.Ordinal)))
            {
                keys.Add(key);
                cleared.Add(value);
            }
        }
        for (var i = 0; i < keys.Count; i++)
        {
            Approvals.Remove(keys[i]);
            UnindexApprovalLocked(cleared[i]);
        }
        return (cleared, _approvalParentsByChild.ContainsKey(childItemId));
    }

    public bool HasSharedThread(string itemA, string itemB)
    {
        if (!_threadsByParticipant.TryGetValue(itemA, out var setA) || setA.Count == 0) return false;
        if (!_threadsByParticipant.TryGetValue(itemB, out var setB) || setB.Count == 0) return false;
        var (smaller, larger) = setA.Count <= setB.Count ? (setA, setB) : (setB, setA);
        foreach (var threadId in smaller)
        {
            if (larger.Contains(threadId)) return true;
        }
        return false;
    }

    public bool HasApproval(string parentItemId, string childItemId) =>
        _approvalParentsByChild.TryGetValue(childItemId, out var parents)
        && parents.ContainsKey(parentItemId);

    public List<CollabThreadRecord> GetThreadsForParticipant(string itemId)
    {
        if (!_threadsByParticipant.TryGetValue(itemId, out var threadIds) || threadIds.Count == 0) return [];
        var result = new List<CollabThreadRecord>(threadIds.Count);
        foreach (var threadId in threadIds)
        {
            if (Threads.TryGetValue(threadId, out var thread))
            {
                result.Add(thread);
            }
        }
        result.Sort(static (a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return result;
    }

    public void CollectRelatedItemsLocked(string changedItemId, HashSet<string> targets)
    {
        if (_threadsByParticipant.TryGetValue(changedItemId, out var threadIds))
        {
            foreach (var threadId in threadIds)
            {
                if (!Threads.TryGetValue(threadId, out var thread)) continue;
                foreach (var participantId in thread.ParticipantItemIds)
                {
                    targets.Add(participantId);
                }
            }
        }
        if (_approvalParentsByChild.TryGetValue(changedItemId, out var parents))
        {
            foreach (var parentId in parents.Keys)
            {
                targets.Add(parentId);
            }
        }
    }

    private void IndexThreadLocked(CollabThreadRecord thread)
    {
        foreach (var participantId in thread.ParticipantItemIds)
        {
            IndexParticipantLocked(participantId, thread.ThreadId);
        }
    }

    private void IndexParticipantLocked(string participantId, string threadId)
    {
        if (string.IsNullOrEmpty(participantId)) return;
        if (!_threadsByParticipant.TryGetValue(participantId, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            _threadsByParticipant[participantId] = set;
        }
        set.Add(threadId);
    }

    private void UnindexThreadLocked(CollabThreadRecord thread)
    {
        foreach (var participantId in thread.ParticipantItemIds)
        {
            if (_threadsByParticipant.TryGetValue(participantId, out var set))
            {
                set.Remove(thread.ThreadId);
                if (set.Count == 0)
                {
                    _threadsByParticipant.Remove(participantId);
                }
            }
        }
    }

    private void IndexApprovalLocked(CollabBubbledApproval approval)
    {
        if (!_approvalParentsByChild.TryGetValue(approval.ChildItemId, out var parents))
        {
            parents = new Dictionary<string, int>(StringComparer.Ordinal);
            _approvalParentsByChild[approval.ChildItemId] = parents;
        }
        parents[approval.ParentItemId] = parents.GetValueOrDefault(approval.ParentItemId) + 1;
    }

    private void UnindexApprovalLocked(CollabBubbledApproval approval)
    {
        if (!_approvalParentsByChild.TryGetValue(approval.ChildItemId, out var parents)) return;
        if (!parents.TryGetValue(approval.ParentItemId, out var count)) return;
        if (count <= 1)
        {
            parents.Remove(approval.ParentItemId);
            if (parents.Count == 0)
            {
                _approvalParentsByChild.Remove(approval.ChildItemId);
            }
        }
        else
        {
            parents[approval.ParentItemId] = count - 1;
        }
    }

    public int GetTaskOwnershipEpoch(string taskId) =>
        _taskOwnershipEpoch.GetValueOrDefault(taskId);

    public string? GetTaskOwnerDelivery(string taskId) =>
        _taskOwningDelivery.GetValueOrDefault(taskId);

    public void BumpTaskOwnershipEpoch(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;
        _taskOwnershipEpoch[taskId] = _taskOwnershipEpoch.GetValueOrDefault(taskId) + 1;
    }

    public void SetTaskOwnerDelivery(string taskId, string? deliveryId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;
        BumpTaskOwnershipEpoch(taskId);
        if (!string.IsNullOrWhiteSpace(deliveryId))
        {
            _taskOwningDelivery[taskId] = deliveryId;
        }
    }

    /// <summary>
    /// Moves a task to <paramref name="status"/>. <paramref name="activity"/> / <paramref name="result"/> replace the
    /// current ones when given. A finished task gets its completion time; a reopened one loses it.
    /// </summary>
    public void SetTaskStatus(CollabTaskRecord task, CollabTaskStatus status, string? activity = null, string? result = null)
    {
        var wasFinished = task.IsFinished;
        task.Status = status;
        _terminalCallerRetiredLinkByTask.Remove(task.TaskId);
        if (activity is not null) task.LatestActivity = activity;
        if (result is not null) task.ResultSummary = result;
        task.CompletedAt = task.IsFinished ? DateTimeOffset.UtcNow : null;
        if (task.IsFinished)
        {
            PruneFinishedOwnershipLinks(task.TaskId);
        }
        if (wasFinished != task.IsFinished)
        {
            _taskOwningDelivery.Remove(task.TaskId);
            BumpTaskOwnershipEpoch(task.TaskId);
        }
        Touch(task);
    }

    public void SetParentTask(string childTaskId, string parentTaskId)
    {
        if (string.IsNullOrWhiteSpace(childTaskId)
            || string.IsNullOrWhiteSpace(parentTaskId)
            || string.Equals(childTaskId, parentTaskId, StringComparison.Ordinal))
        {
            return;
        }
        _parentTaskByChildTask[childTaskId] = parentTaskId;
        BumpTaskOwnershipEpoch(parentTaskId);
    }

    public void SetExternalTurnOwner(string childTaskId, string externalTurnKey)
    {
        if (string.IsNullOrWhiteSpace(childTaskId) || string.IsNullOrWhiteSpace(externalTurnKey))
        {
            return;
        }
        _externalTurnByChildTask[childTaskId] = externalTurnKey;
    }

    public bool HasUnfinishedExternalTurnChild(string externalTurnKey)
    {
        foreach (var (childId, turnKey) in _externalTurnByChildTask)
        {
            if (string.Equals(turnKey, externalTurnKey, StringComparison.Ordinal)
                && Tasks.TryGetValue(childId, out var child)
                && !child.IsFinished)
            {
                return true;
            }
        }
        return false;
    }

    public string? GetParentTaskId(string childTaskId) =>
        _parentTaskByChildTask.GetValueOrDefault(childTaskId);

    public bool HasUnfinishedChildTask(string parentTaskId)
    {
        foreach (var (childId, ownerId) in _parentTaskByChildTask)
        {
            if (string.Equals(ownerId, parentTaskId, StringComparison.Ordinal)
                && Tasks.TryGetValue(childId, out var child)
                && !child.IsFinished)
            {
                return true;
            }
        }
        return false;
    }

    public List<string> GetUnfinishedChildTaskIds(string parentTaskId)
    {
        List<string> children = [];
        foreach (var (childId, ownerId) in _parentTaskByChildTask)
        {
            if (string.Equals(ownerId, parentTaskId, StringComparison.Ordinal)
                && Tasks.TryGetValue(childId, out var child)
                && !child.IsFinished)
            {
                children.Add(childId);
            }
        }
        return children;
    }

    public void SetCallerTerminalLink(string taskId, string callerLinkId)
    {
        if (string.IsNullOrWhiteSpace(taskId) || string.IsNullOrWhiteSpace(callerLinkId)) return;
        _callerTerminalLinkByTask[taskId] = callerLinkId;
    }

    public string? GetCallerTerminalLink(string taskId) =>
        _callerTerminalLinkByTask.GetValueOrDefault(taskId);

    public void RemoveCallerTerminalLink(string taskId) =>
        _callerTerminalLinkByTask.Remove(taskId);

    public void MarkCancelledByCallerTerminalRetirement(string taskId, string? retiredCallerLinkId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return;
        _terminalCallerRetiredLinkByTask[taskId] = retiredCallerLinkId ?? string.Empty;
    }

    public bool CanReopenFromCallerTerminalRetirement(string taskId, string callerTerminalLinkId) =>
        !string.IsNullOrWhiteSpace(taskId)
        && !string.IsNullOrWhiteSpace(callerTerminalLinkId)
        && _terminalCallerRetiredLinkByTask.TryGetValue(taskId, out var retiredLinkId)
        && !string.Equals(retiredLinkId, callerTerminalLinkId, StringComparison.Ordinal);

    public bool HasUnacceptedInitialDelegation(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)) return false;
        foreach (var list in Pending.Values)
        {
            foreach (var p in list)
            {
                if (!p.Accepted
                    && !p.SettledExplicitly
                    && p.ExpectedInitialTaskOwnershipEpoch is not null
                    && string.Equals(p.TaskId, taskId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public bool HasUnacceptedInitialDelegationForThread(string threadId, string callerItemId)
    {
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(callerItemId)) return false;
        foreach (var list in Pending.Values)
        {
            foreach (var p in list)
            {
                if (!p.Accepted
                    && !p.SettledExplicitly
                    && p.ExpectedInitialTaskOwnershipEpoch is not null
                    && string.Equals(p.ThreadId, threadId, StringComparison.Ordinal)
                    && string.Equals(p.CallerItemId, callerItemId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public List<string> GetUnfinishedOutboundTasksForCallerLinks(string callerItemId, IReadOnlySet<string> callerLinkIds)
    {
        List<string> result = [];
        List<string>? stale = null;
        foreach (var (taskId, linkId) in _callerTerminalLinkByTask)
        {
            if (!Tasks.TryGetValue(taskId, out var task) || task.IsFinished)
            {
                (stale ??= []).Add(taskId);
                continue;
            }
            if (callerLinkIds.Contains(linkId)
                && string.Equals(task.CallerItemId, callerItemId, StringComparison.Ordinal))
            {
                result.Add(taskId);
            }
        }
        if (stale is not null)
        {
            foreach (var taskId in stale)
            {
                _callerTerminalLinkByTask.Remove(taskId);
            }
        }
        return result;
    }

    private void PruneFinishedOwnershipLinks(string finishedTaskId)
    {
        _externalTurnByChildTask.Remove(finishedTaskId);
        _callerTerminalLinkByTask.Remove(finishedTaskId);
        if (_parentTaskByChildTask.TryGetValue(finishedTaskId, out var parentId)
            && (!Tasks.TryGetValue(parentId, out var parent) || parent.IsFinished))
        {
            _parentTaskByChildTask.Remove(finishedTaskId);
        }

        List<string>? removableChildren = null;
        foreach (var (childId, ownerId) in _parentTaskByChildTask)
        {
            if (string.Equals(ownerId, finishedTaskId, StringComparison.Ordinal)
                && (!Tasks.TryGetValue(childId, out var child) || child.IsFinished))
            {
                (removableChildren ??= []).Add(childId);
            }
        }
        if (removableChildren is not null)
        {
            foreach (var childId in removableChildren)
            {
                _parentTaskByChildTask.Remove(childId);
            }
        }
    }

    public long GetThreadTurnRevision(string threadId) =>
        _threadTurnRevision.GetValueOrDefault(threadId);

    /// <summary>
    /// Advances a thread's owning-turn revision when a round is cancelled, interrupted, explicitly settled, or
    /// superseded by an accepted owning delivery. Informational interjections and non-owning messages do not call this.
    /// </summary>
    public long MarkThreadTurnSuperseded(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId)) return 0;
        var next = _threadTurnRevision.GetValueOrDefault(threadId) + 1;
        _threadTurnRevision[threadId] = next;
        return next;
    }

    public void SetThreadStatus(CollabThreadRecord thread, CollabThreadStatus status)
    {
        thread.Status = status;
        if (status is CollabThreadStatus.Cancelled or CollabThreadStatus.Interrupted)
        {
            MarkThreadTurnSuperseded(thread.ThreadId);
        }
        Touch(thread);
    }

    public void AppendMessage(CollabThreadRecord thread, CollabMessageRecord message)
    {
        thread.Messages.Add(message);
        Touch(thread);
    }

    /// <summary>For changes the methods above do not cover (budget, participants): a new sequence number and a save.</summary>
    public void Touch(CollabTaskRecord task)
    {
        task.UpdatedSeq = ++_eventSeq;
        _dirtyVersion++;
    }

    public void Touch(CollabThreadRecord thread)
    {
        thread.UpdatedSeq = ++_eventSeq;
        thread.UpdatedAt = DateTimeOffset.UtcNow;
        _dirtyVersion++;
    }

    /// <summary>Records what await_events just reported to <paramref name="callerItemId"/>.</summary>
    public void MarkSeen(string callerItemId, IEnumerable<(string Id, long Seq)> reported)
    {
        if (!Seen.TryGetValue(callerItemId, out var map))
        {
            map = new Dictionary<string, long>(StringComparer.Ordinal);
            Seen[callerItemId] = map;
        }
        foreach (var (id, seq) in reported)
        {
            if (seq > map.GetValueOrDefault(id)) map[id] = seq;
        }
        _dirtyVersion++;
    }

    /* ---------- pending turn expectations (hold Gate) ---------- */

    public void EnqueuePending(string targetItemId, PendingExpectation expectation)
    {
        if (!Pending.TryGetValue(targetItemId, out var list))
        {
            list = [];
            Pending[targetItemId] = list;
        }
        list.Add(expectation);
    }

    public bool HasPendingReference(string targetItemId, PendingExpectation exact) =>
        Pending.TryGetValue(targetItemId, out var list)
        && list.Any(p => ReferenceEquals(p, exact) && !p.SettledExplicitly);

    public PendingExpectation? FindPendingForDelivery(string targetItemId, string? threadId, string? taskId, string? deliveryId)
    {
        if (!Pending.TryGetValue(targetItemId, out var list) || list.Count == 0) return null;
        var idx = FindMatchingPendingIndex(list, threadId, taskId, deliveryId, unsettledOnly: true);
        return idx >= 0 ? list[idx] : null;
    }

    public long GetLatestAcceptedDeliverySeq(string targetItemId, string? threadId) =>
        !string.IsNullOrWhiteSpace(threadId)
            ? _latestAcceptedSeqByTargetThread.GetValueOrDefault((targetItemId, threadId))
            : 0;

    public bool HasNewerAcceptedDelivery(string targetItemId, string? threadId, long acceptedSeq) =>
        !string.IsNullOrWhiteSpace(threadId)
        && _latestAcceptedSeqByTargetThread.TryGetValue((targetItemId, threadId), out var latestSeq)
        && latestSeq > acceptedSeq;

    public bool HasNewerDispatchedDelivery(string targetItemId, string? threadId, long acceptedSeq) =>
        !string.IsNullOrWhiteSpace(threadId)
        && _latestDispatchedSeqByTargetThread.TryGetValue((targetItemId, threadId), out var latestSeq)
        && latestSeq > acceptedSeq;

    public bool HasNewerAcceptedDelivery(string targetItemId, string threadId, PendingExpectation abandoned) =>
        HasNewerAcceptedDelivery(targetItemId, threadId, abandoned.AcceptedSeq);

    public bool HasNewerAcceptedPendingOnDifferentBridge(string targetItemId, string? threadId, PendingExpectation completed)
    {
        if (string.IsNullOrWhiteSpace(threadId)
            || !Pending.TryGetValue(targetItemId, out var list)
            || list.Count == 0)
        {
            return false;
        }
        return list.Any(other =>
            !ReferenceEquals(other, completed)
            && !other.SettledExplicitly
            && other.Accepted
            && string.Equals(other.ThreadId, threadId, StringComparison.Ordinal)
            && other.AcceptedSeq > completed.AcceptedSeq
            && other.BridgeOwner is not null
            && !ReferenceEquals(other.BridgeOwner, completed.BridgeOwner));
    }

    public string? FindDispatchedPendingDeliveryId(
        string targetItemId,
        string? threadId,
        string? taskId,
        IReadOnlySet<string> candidateDeliveryIds)
    {
        if (candidateDeliveryIds.Count == 0
            || (string.IsNullOrWhiteSpace(threadId) && string.IsNullOrWhiteSpace(taskId))
            || !Pending.TryGetValue(targetItemId, out var list)
            || list.Count == 0)
        {
            return null;
        }
        var hasThread = !string.IsNullOrWhiteSpace(threadId);
        var hasTask = !string.IsNullOrWhiteSpace(taskId);
        foreach (var p in list)
        {
            if (!p.Dispatched
                || p.SettledExplicitly
                || string.IsNullOrWhiteSpace(p.DeliveryId)
                || !candidateDeliveryIds.Contains(p.DeliveryId))
            {
                continue;
            }
            if (hasTask && !string.Equals(p.TaskId, taskId, StringComparison.Ordinal)) continue;
            if (hasThread && !string.Equals(p.ThreadId, threadId, StringComparison.Ordinal)) continue;
            return p.DeliveryId;
        }
        return null;
    }

    public string? FindPendingDeliveryIdByIngressToken(string targetItemId, long ingressToken)
    {
        if (ingressToken <= 0 || !Pending.TryGetValue(targetItemId, out var list) || list.Count == 0)
        {
            return null;
        }
        foreach (var p in list)
        {
            if (!p.SettledExplicitly && p.IngressToken == ingressToken && !string.IsNullOrWhiteSpace(p.DeliveryId))
            {
                return p.DeliveryId;
            }
        }
        return null;
    }

    public void UpdatePendingBridgeOwner(string targetItemId, string? deliveryId, object newBridgeOwner)
    {
        if (string.IsNullOrWhiteSpace(deliveryId) || !Pending.TryGetValue(targetItemId, out var list)) return;
        foreach (var p in list)
        {
            if (string.Equals(p.DeliveryId, deliveryId, StringComparison.Ordinal))
            {
                p.BridgeOwner = newBridgeOwner;
            }
        }
    }

    public bool HasActiveWakeupByDeliveryId(string recipientItemId, string? deliveryId) =>
        !string.IsNullOrWhiteSpace(deliveryId)
        && ActiveWakeups.TryGetValue(recipientItemId, out var list)
        && list.Any(w => !w.Revoked && string.Equals(w.DeliveryId, deliveryId, StringComparison.Ordinal));

    public bool TryDiscardUnacceptedPendingByDeliveryId(string targetItemId, string deliveryId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId) || !Pending.TryGetValue(targetItemId, out var list) || list.Count == 0)
        {
            return false;
        }
        var idx = list.FindIndex(p => string.Equals(p.DeliveryId, deliveryId, StringComparison.Ordinal));
        if (idx < 0 || list[idx].Accepted || list[idx].Dispatched || list[idx].SettledExplicitly)
        {
            return false;
        }
        list.RemoveAt(idx);
        PrunePending(targetItemId, list);
        RemoveActiveWakeupByDeliveryId(targetItemId, deliveryId);
        return true;
    }

    public bool HasActivePendingDelivery(string targetItemId, string? deliveryId, string? threadId, string? taskId)
    {
        if (!Pending.TryGetValue(targetItemId, out var list) || list.Count == 0) return false;
        if (!string.IsNullOrWhiteSpace(deliveryId))
        {
            return list.Any(p => !p.SettledExplicitly && string.Equals(p.DeliveryId, deliveryId, StringComparison.Ordinal));
        }
        var hasThread = !string.IsNullOrWhiteSpace(threadId);
        var hasTask = !string.IsNullOrWhiteSpace(taskId);
        if (hasThread && hasTask)
        {
            return list.Any(p =>
                !p.SettledExplicitly
                && string.Equals(p.ThreadId, threadId, StringComparison.Ordinal)
                && string.Equals(p.TaskId, taskId, StringComparison.Ordinal));
        }
        if (hasTask)
        {
            return list.Any(p => !p.SettledExplicitly && string.Equals(p.TaskId, taskId, StringComparison.Ordinal));
        }
        if (hasThread)
        {
            return list.Any(p => !p.SettledExplicitly && string.Equals(p.ThreadId, threadId, StringComparison.Ordinal));
        }
        return false;
    }

    public void AddActiveWakeup(string recipientItemId, QueuedWakeup wakeup)
    {
        if (!ActiveWakeups.TryGetValue(recipientItemId, out var list))
        {
            list = [];
            ActiveWakeups[recipientItemId] = list;
        }
        list.Add(wakeup);
    }

    public bool HasActiveWakeupReference(string recipientItemId, QueuedWakeup exact) =>
        ActiveWakeups.TryGetValue(recipientItemId, out var list)
        && list.Any(w => ReferenceEquals(w, exact));

    public void RemoveActiveWakeup(string recipientItemId, QueuedWakeup exact)
    {
        exact.Revoked = true;
        RetireActiveWakeup(recipientItemId, exact);
    }

    public void RetireActiveWakeup(string recipientItemId, QueuedWakeup exact)
    {
        if (!ActiveWakeups.TryGetValue(recipientItemId, out var list)) return;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(list[i], exact))
            {
                list.RemoveAt(i);
                break;
            }
        }
        if (list.Count == 0)
        {
            ActiveWakeups.Remove(recipientItemId);
        }
    }

    public void MarkWakeupManagedLifetime(string recipientItemId, string? deliveryId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId) || !ActiveWakeups.TryGetValue(recipientItemId, out var list)) return;
        foreach (var w in list)
        {
            if (string.Equals(w.DeliveryId, deliveryId, StringComparison.Ordinal))
            {
                w.ManagedLifetime = true;
            }
        }
    }

    public void RemoveActiveWakeupByDeliveryId(string recipientItemId, string? deliveryId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId) || !ActiveWakeups.TryGetValue(recipientItemId, out var list)) return;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (string.Equals(list[i].DeliveryId, deliveryId, StringComparison.Ordinal))
            {
                list[i].Revoked = true;
                list.RemoveAt(i);
            }
        }
        if (list.Count == 0)
        {
            ActiveWakeups.Remove(recipientItemId);
        }
    }

    public bool IsDeliveryDispatched(string recipientItemId, string? deliveryId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId)) return false;
        if (ActiveWakeups.TryGetValue(recipientItemId, out var wakeups)
            && wakeups.Any(w => w.Dispatched && string.Equals(w.DeliveryId, deliveryId, StringComparison.Ordinal)))
        {
            return true;
        }
        if (Pending.TryGetValue(recipientItemId, out var pendingList)
            && pendingList.Any(p => p.Dispatched && !p.SettledExplicitly && string.Equals(p.DeliveryId, deliveryId, StringComparison.Ordinal)))
        {
            return true;
        }
        return false;
    }

    public void MarkDeliveryDispatched(string recipientItemId, string? deliveryId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId)) return;
        if (ActiveWakeups.TryGetValue(recipientItemId, out var wakeups))
        {
            foreach (var w in wakeups)
            {
                if (string.Equals(w.DeliveryId, deliveryId, StringComparison.Ordinal))
                {
                    w.Dispatched = true;
                }
            }
        }
        if (Pending.TryGetValue(recipientItemId, out var pendingList))
        {
            foreach (var p in pendingList)
            {
                if (string.Equals(p.DeliveryId, deliveryId, StringComparison.Ordinal))
                {
                    p.Dispatched = true;
                    if (p.Accepted)
                    {
                        RecordDispatchedDelivery(recipientItemId, p.ThreadId, p.AcceptedSeq);
                    }
                }
            }
        }
    }

    public long GetSessionEpoch(string itemId) =>
        _sessionEpoch.GetValueOrDefault(itemId);

    public bool IsWakeupReadConsumed(string recipientItemId, QueuedWakeup wakeup)
    {
        if (wakeup.Dispatched || !_readObservedSeq.TryGetValue(recipientItemId, out var observed))
        {
            return false;
        }
        if (observed.TryGetValue(wakeup.ThreadId, out var obsThreadSeq) && wakeup.ReplySeq <= obsThreadSeq)
        {
            return true;
        }
        return wakeup.MatchingTasks.Count > 0
            && wakeup.MatchingTasks.All(t => observed.TryGetValue(t.TaskId, out var obsTaskSeq) && t.Seq <= obsTaskSeq);
    }

    /// <summary>
    /// Marks reply wakeups at or below the snapshot's observed sequences as consumed, removing both their active wakeup
    /// entries and their matching <see cref="PendingExpectation"/>s without touching later wakeups, direct delegations,
    /// or the currently executing turn. Hold <see cref="Gate"/>.
    /// </summary>
    public List<(string ThreadId, string DeliveryId)> ConsumeWakeupsAtOrBelow(
        string recipientItemId,
        IReadOnlyDictionary<string, long> observedThreadSeqs,
        IReadOnlyDictionary<string, long> observedTaskSeqs,
        string? activeDeliveryId = null)
    {
        if (observedThreadSeqs.Count > 0 || observedTaskSeqs.Count > 0)
        {
            if (!_readObservedSeq.TryGetValue(recipientItemId, out var observed))
            {
                observed = new Dictionary<string, long>(StringComparer.Ordinal);
                _readObservedSeq[recipientItemId] = observed;
            }
            foreach (var (tid, seq) in observedThreadSeqs)
            {
                if (seq > observed.GetValueOrDefault(tid)) observed[tid] = seq;
            }
            foreach (var (taskId, seq) in observedTaskSeqs)
            {
                if (seq > observed.GetValueOrDefault(taskId)) observed[taskId] = seq;
            }
        }

        List<(string ThreadId, string DeliveryId)> consumed = [];
        HashSet<string>? consumedDeliveryIds = null;

        if (ActiveWakeups.TryGetValue(recipientItemId, out var wakeups))
        {
            for (var i = wakeups.Count - 1; i >= 0; i--)
            {
                var w = wakeups[i];
                if (w.Dispatched
                    || (!string.IsNullOrWhiteSpace(activeDeliveryId)
                        && string.Equals(w.DeliveryId, activeDeliveryId, StringComparison.Ordinal)))
                {
                    continue;
                }
                var coveredByThread = observedThreadSeqs.TryGetValue(w.ThreadId, out var obsThreadSeq)
                    && w.ReplySeq <= obsThreadSeq;
                var coveredByTasks = w.MatchingTasks.Count > 0
                    && w.MatchingTasks.All(t => observedTaskSeqs.TryGetValue(t.TaskId, out var obsTaskSeq) && t.Seq <= obsTaskSeq);
                if (!coveredByThread && !coveredByTasks) continue;

                w.Revoked = true;
                wakeups.RemoveAt(i);
                consumed.Add((w.ThreadId, w.DeliveryId));
                (consumedDeliveryIds ??= new HashSet<string>(StringComparer.Ordinal)).Add(w.DeliveryId);
            }
            if (wakeups.Count == 0)
            {
                ActiveWakeups.Remove(recipientItemId);
            }
        }

        if (Pending.TryGetValue(recipientItemId, out var pendingList))
        {
            for (var i = pendingList.Count - 1; i >= 0; i--)
            {
                var p = pendingList[i];
                if (p.SettledExplicitly || p.Dispatched || p.WakeupSeq is not { } wSeq) continue;
                if (!string.IsNullOrWhiteSpace(activeDeliveryId)
                    && string.Equals(p.DeliveryId, activeDeliveryId, StringComparison.Ordinal))
                {
                    continue;
                }
                var matchedByDelivery = consumedDeliveryIds is not null
                    && !string.IsNullOrWhiteSpace(p.DeliveryId)
                    && consumedDeliveryIds.Contains(p.DeliveryId);
                var matchedByThreadSeq = p.ThreadId is { } tid
                    && observedThreadSeqs.TryGetValue(tid, out var obsSeq)
                    && wSeq <= obsSeq;
                if (matchedByDelivery || matchedByThreadSeq)
                {
                    pendingList.RemoveAt(i);
                }
            }
            PrunePending(recipientItemId, pendingList);
        }

        consumed.Reverse();
        return consumed;
    }

    public void RemovePending(string targetItemId, PendingExpectation exact)
    {
        if (!Pending.TryGetValue(targetItemId, out var list)) return;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(list[i], exact))
            {
                list.RemoveAt(i);
                break;
            }
        }
        PrunePending(targetItemId, list);
    }

    public PendingExpectation? FindPendingForExplicitSettlement(string targetItemId, string threadId, string? deliveryId = null)
    {
        if (!Pending.TryGetValue(targetItemId, out var list)) return null;
        var idx = -1;
        if (!string.IsNullOrWhiteSpace(deliveryId))
        {
            idx = list.FindIndex(p =>
                !p.SettledExplicitly
                && string.Equals(p.DeliveryId, deliveryId, StringComparison.Ordinal)
                && string.Equals(p.ThreadId, threadId, StringComparison.Ordinal));
        }
        if (idx < 0)
        {
            idx = list.FindIndex(p => !p.SettledExplicitly && string.Equals(p.ThreadId, threadId, StringComparison.Ordinal));
        }
        return idx >= 0 ? list[idx] : null;
    }

    public PendingExpectation? MarkPendingDeferred(string targetItemId, string? threadId, string? taskId, string? deliveryId = null)
    {
        if (!Pending.TryGetValue(targetItemId, out var list) || list.Count == 0) return null;
        var idx = FindMatchingPendingIndex(list, threadId, taskId, deliveryId, unsettledOnly: true);
        if (idx < 0) return null;
        var matched = list[idx];
        matched.Deferred = true;
        return matched;
    }

    public PendingExpectation? TakePendingForTurn(string targetItemId, string? threadId, string? taskId, bool hasTurnIdentity, string? deliveryId = null)
    {
        if (!Pending.TryGetValue(targetItemId, out var list) || list.Count == 0)
        {
            if (hasTurnIdentity && !string.IsNullOrWhiteSpace(deliveryId))
            {
                RemoveActiveWakeupByDeliveryId(targetItemId, deliveryId);
            }
            return null;
        }

        if (!hasTurnIdentity)
        {
            var idxUncorrelated = list.FindIndex(p => !p.Deferred);
            if (idxUncorrelated < 0) return null;
            var head = list[idxUncorrelated];
            list.RemoveAt(idxUncorrelated);
            PrunePending(targetItemId, list);
            RemoveActiveWakeupByDeliveryId(targetItemId, head.DeliveryId);
            return head.SettledExplicitly ? null : head;
        }

        var hasDelivery = !string.IsNullOrWhiteSpace(deliveryId);
        var hasThread = !string.IsNullOrWhiteSpace(threadId);
        var hasTask = !string.IsNullOrWhiteSpace(taskId);
        if (!hasDelivery && !hasThread && !hasTask) return null;

        var idx = FindMatchingPendingIndex(list, threadId, taskId, deliveryId, unsettledOnly: false);
        if (idx < 0)
        {
            if (hasDelivery)
            {
                RemoveActiveWakeupByDeliveryId(targetItemId, deliveryId);
            }
            return null;
        }

        var matched = list[idx];
        list.RemoveAt(idx);
        PrunePending(targetItemId, list);
        RemoveActiveWakeupByDeliveryId(targetItemId, matched.DeliveryId ?? deliveryId);
        return matched.SettledExplicitly ? null : matched;
    }

    public bool SetPendingOwnerLink(string targetItemId, string deliveryId, string ownerLinkId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId)
            || string.IsNullOrWhiteSpace(ownerLinkId)
            || !Pending.TryGetValue(targetItemId, out var pendingList))
        {
            return false;
        }
        var matched = false;
        foreach (var p in pendingList)
        {
            if (!p.SettledExplicitly && string.Equals(p.DeliveryId, deliveryId, StringComparison.Ordinal))
            {
                p.OwningTerminalLinkId = ownerLinkId;
                matched = true;
            }
        }
        return matched;
    }

    public List<PendingExpectation> TakeAllPending(
        string targetItemId,
        IReadOnlySet<string>? onlyDeliveryIds = null,
        IReadOnlySet<string>? onlyOwnerLinkIds = null)
    {
        if (onlyDeliveryIds is null && onlyOwnerLinkIds is null)
        {
            _sessionEpoch[targetItemId] = _sessionEpoch.GetValueOrDefault(targetItemId) + 1;
            if (ActiveWakeups.Remove(targetItemId, out var wakeups))
            {
                foreach (var w in wakeups)
                {
                    w.Revoked = true;
                }
            }
            if (!Pending.Remove(targetItemId, out var list)) return [];
            return list.Where(p => !p.SettledExplicitly).ToList();
        }
        if (onlyDeliveryIds is not null)
        {
            foreach (var deliveryId in onlyDeliveryIds)
            {
                RemoveActiveWakeupByDeliveryId(targetItemId, deliveryId);
            }
        }
        if (!Pending.TryGetValue(targetItemId, out var pendingList) || pendingList.Count == 0)
        {
            return [];
        }
        List<PendingExpectation> taken = [];
        for (var i = pendingList.Count - 1; i >= 0; i--)
        {
            var p = pendingList[i];
            var matchesOwnerLink = onlyOwnerLinkIds is not null
                && !string.IsNullOrWhiteSpace(p.OwningTerminalLinkId)
                && onlyOwnerLinkIds.Contains(p.OwningTerminalLinkId);
            var matchesDelivery = onlyDeliveryIds is not null
                && (string.IsNullOrWhiteSpace(p.DeliveryId) || onlyDeliveryIds.Contains(p.DeliveryId));
            if (!matchesOwnerLink && !matchesDelivery)
            {
                continue;
            }
            pendingList.RemoveAt(i);
            RemoveActiveWakeupByDeliveryId(targetItemId, p.DeliveryId);
            if (!p.SettledExplicitly)
            {
                taken.Add(p);
            }
        }
        PrunePending(targetItemId, pendingList);
        taken.Reverse();
        return taken;
    }

    public (bool WasPending, bool WasHead, bool HasEarlierPending, bool WasDeferred, string? RemovedDeliveryId) RemovePendingForTask(string targetItemId, string? threadId, string? taskId)
    {
        if (!Pending.TryGetValue(targetItemId, out var list) || list.Count == 0)
        {
            return (false, false, false, false, null);
        }

        var firstIdx = FindMatchingPendingIndex(list, threadId, taskId, deliveryId: null, unsettledOnly: true);
        if (firstIdx < 0)
        {
            return (false, false, list.Any(p => !p.SettledExplicitly && !p.Deferred), false, null);
        }

        var hasEarlier = false;
        for (var i = 0; i < firstIdx; i++)
        {
            if (!list[i].SettledExplicitly && !list[i].Deferred)
            {
                hasEarlier = true;
                break;
            }
        }
        string? removedDeliveryId = null;
        var removedCount = 0;
        var anyNonDeferred = false;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (!list[i].SettledExplicitly && MatchesPendingForCancel(list[i], threadId, taskId))
            {
                var delId = list[i].DeliveryId;
                removedDeliveryId = removedCount == 0 ? delId : null;
                removedCount++;
                if (!list[i].Deferred) anyNonDeferred = true;
                list.RemoveAt(i);
                RemoveActiveWakeupByDeliveryId(targetItemId, delId);
            }
        }
        var wasDeferred = removedCount > 0 && !anyNonDeferred;
        var wasHead = !wasDeferred && !hasEarlier;
        PrunePending(targetItemId, list);
        return (true, wasHead, hasEarlier, wasDeferred, removedDeliveryId);
    }

    private static bool MatchesPendingForCancel(PendingExpectation pending, string? threadId, string? taskId)
    {
        var hasThread = !string.IsNullOrWhiteSpace(threadId);
        var hasTask = !string.IsNullOrWhiteSpace(taskId);
        if (hasThread && hasTask)
        {
            return (pending.TaskId is null || string.Equals(pending.TaskId, taskId, StringComparison.Ordinal))
                && string.Equals(pending.ThreadId, threadId, StringComparison.Ordinal);
        }
        if (hasTask)
        {
            return string.Equals(pending.TaskId, taskId, StringComparison.Ordinal);
        }
        if (hasThread)
        {
            return string.Equals(pending.ThreadId, threadId, StringComparison.Ordinal);
        }
        return false;
    }

    private static int FindMatchingPendingIndex(
        List<PendingExpectation> list,
        string? threadId,
        string? taskId,
        string? deliveryId = null,
        bool unsettledOnly = false)
    {
        var hasDelivery = !string.IsNullOrWhiteSpace(deliveryId);
        var hasThread = !string.IsNullOrWhiteSpace(threadId);
        var hasTask = !string.IsNullOrWhiteSpace(taskId);

        if (hasDelivery)
        {
            return list.FindIndex(p =>
                (!unsettledOnly || !p.SettledExplicitly)
                && string.Equals(p.DeliveryId, deliveryId, StringComparison.Ordinal)
                && (!hasTask || p.TaskId is null || string.Equals(p.TaskId, taskId, StringComparison.Ordinal))
                && (!hasThread || p.ThreadId is null || string.Equals(p.ThreadId, threadId, StringComparison.Ordinal)));
        }

        if (hasThread && hasTask)
        {
            return list.FindIndex(p =>
                (!unsettledOnly || !p.SettledExplicitly)
                && string.Equals(p.TaskId, taskId, StringComparison.Ordinal)
                && string.Equals(p.ThreadId, threadId, StringComparison.Ordinal));
        }
        if (hasTask)
        {
            var byTask = list.FindIndex(p =>
                (!unsettledOnly || !p.SettledExplicitly)
                && string.Equals(p.TaskId, taskId, StringComparison.Ordinal));
            if (byTask >= 0) return byTask;
        }
        if (hasThread)
        {
            var byThread = list.FindIndex(p =>
                (!unsettledOnly || !p.SettledExplicitly)
                && string.Equals(p.ThreadId, threadId, StringComparison.Ordinal));
            if (byThread >= 0) return byThread;
        }
        return -1;
    }

    private void PrunePending(string targetItemId, List<PendingExpectation> list)
    {
        if (list.Count == 0)
        {
            Pending.Remove(targetItemId);
        }
    }

    /* ---------- waiters (any thread) ---------- */

    public int WaiterCount
    {
        get
        {
            lock (Gate) return Waiters.Count;
        }
    }

    /// <summary>True when <paramref name="callerItemId"/> has already consumed this reply via <c>await_events</c>. Hold <see cref="Gate"/>.</summary>
    public bool IsReplyConsumed(string callerItemId, string threadId, long replySeq, IReadOnlyList<(string TaskId, long Seq)> matchingTasks)
    {
        if (!Seen.TryGetValue(callerItemId, out var seen)) return false;
        if (seen.GetValueOrDefault(threadId) >= replySeq) return true;
        return matchingTasks.Count > 0 && matchingTasks.All(t => seen.GetValueOrDefault(t.TaskId) >= t.Seq);
    }

    /// <summary>Wakes every active blocked await_events so it looks again, returning the waiters that were actually signaled.</summary>
    public List<EventWaiter> SignalWaiters()
    {
        List<EventWaiter> copy;
        lock (Gate)
        {
            if (Waiters.Count == 0) return [];
            copy = [.. Waiters];
        }
        List<EventWaiter> signaled = [];
        foreach (var w in copy)
        {
            if (w.IsCancelled) continue;
            if ((w.Tcs.TrySetResult(true) || w.Tcs.Task.IsCompletedSuccessfully) && !w.IsCancelled)
            {
                signaled.Add(w);
            }
        }
        return signaled;
    }

    /* ---------- persistence ---------- */

    /// <summary>Saves soon (debounced) when something changed.</summary>
    public void SchedulePersist()
    {
        if (_store is null) return;
        lock (Gate)
        {
            if (_disposed || _dirtyVersion <= _persistedVersion) return;
            if (_consecutivePersistFailures > 0)
            {
                if (_dirtyVersion <= _failedRetryVersion) return;
                _consecutivePersistFailures = 0;
                _failedRetryVersion = 0;
            }
            ArmTimerLocked(PersistDebounceMs);
        }
    }

    public void Flush() => FlushCore(isDisposing: false);

    private void FlushCore(bool isDisposing)
    {
        if (_store is null) return;
        lock (_persistGate)
        {
            string json;
            long snapshotVersion;
            lock (Gate)
            {
                if (_disposed && !isDisposing) return;
                if (_dirtyVersion <= _persistedVersion)
                {
                    _consecutivePersistFailures = 0;
                    _failedRetryVersion = 0;
                    if (!_disposed) _persistTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    return;
                }
                snapshotVersion = _dirtyVersion;
                json = JsonSerializer.Serialize(ToDocument(), Json.CollabLedgerDocument);
            }

            var saved = _store.Save(json);

            lock (Gate)
            {
                if (saved)
                {
                    if (snapshotVersion > _persistedVersion) _persistedVersion = snapshotVersion;
                    _consecutivePersistFailures = 0;
                    _failedRetryVersion = 0;
                    if (!_disposed && _dirtyVersion <= _persistedVersion)
                    {
                        _persistTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    }
                }
                else
                {
                    if (_dirtyVersion > snapshotVersion)
                    {
                        // A newer dirty generation arrived while this older snapshot was failing to write.
                        // Charge the failure to the captured snapshot and give the newer generation a fresh schedule.
                        _consecutivePersistFailures = 0;
                        _failedRetryVersion = snapshotVersion;
                        if (!_disposed && !isDisposing)
                        {
                            ArmTimerLocked(PersistDebounceMs);
                        }
                    }
                    else
                    {
                        if (snapshotVersion > _failedRetryVersion)
                        {
                            _consecutivePersistFailures = 1;
                            _failedRetryVersion = snapshotVersion;
                        }
                        else
                        {
                            _consecutivePersistFailures++;
                        }
                        if (!_disposed && !isDisposing && _consecutivePersistFailures <= MaxPersistRetries)
                        {
                            ArmTimerLocked(ComputeRetryDelayMs(_consecutivePersistFailures));
                        }
                    }
                }
            }
        }
    }

    private void ArmTimerLocked(int dueTimeMs)
    {
        _persistTimer ??= new Timer(_ => FlushCore(isDisposing: false), null, Timeout.Infinite, Timeout.Infinite);
        _persistTimer.Change(dueTimeMs, Timeout.Infinite);
    }

    private static int ComputeRetryDelayMs(int failureCount)
    {
        var shift = Math.Clamp(failureCount - 1, 0, 10);
        var delay = (long)PersistRetryBaseMs << shift;
        return (int)Math.Min(delay, PersistRetryMaxMs);
    }

    /// <summary>
    /// Loads the saved ledger. Nothing that was running survives a restart (agent turns are not resumed), so open tasks
    /// become cancelled and open threads interrupted, each with a new sequence number so its delegator is told once.
    /// </summary>
    public void Restore()
    {
        if (_store?.LoadText() is not { Length: > 0 } text) return;
        CollabLedgerDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize(text, Json.CollabLedgerDocument);
        }
        catch (JsonException)
        {
            return; // a damaged ledger must not keep the app from starting; it is overwritten on the next change
        }
        if (doc is null || doc.Version > CollabLedgerDocument.CurrentVersion) return;

        lock (Gate)
        {
            FromDocument(doc);
        }
        SchedulePersist();
    }

    private CollabLedgerDocument ToDocument()
    {
        var cutoff = DateTimeOffset.UtcNow - PersistRetention;
        var tasks = Tasks.Values
            .Where(t => !t.IsFinished || (t.CompletedAt ?? t.CreatedAt) >= cutoff)
            .OrderByDescending(t => t.CreatedAt)
            .Take(MaxPersistedTasks)
            .ToList();
        var threads = Threads.Values
            .Where(t => t.UpdatedAt >= cutoff)
            .OrderByDescending(t => t.UpdatedAt)
            .Take(MaxPersistedThreads)
            .ToList();
        var kept = new HashSet<string>(tasks.Select(t => t.TaskId).Concat(threads.Select(t => t.ThreadId)), StringComparer.Ordinal);

        return new CollabLedgerDocument
        {
            IdSeq = Volatile.Read(ref _idSeq),
            EventSeq = _eventSeq,
            Tasks = [.. tasks.Select(t => new CollabTaskDocument
            {
                TaskId = t.TaskId,
                Caller = t.CallerItemId,
                Target = t.TargetItemId,
                Agent = t.TargetAgentId,
                Title = t.Title,
                Goal = t.Goal,
                Isolation = CollabCoordinator.IsolationToKey(t.Isolation),
                WorktreeBranch = t.WorktreeBranch,
                WorktreePath = t.WorktreePath,
                ThreadId = t.ThreadId,
                Status = t.Status.ToString(),
                Activity = t.LatestActivity,
                Result = t.ResultSummary,
                Created = t.CreatedAt,
                Completed = t.CompletedAt,
                Seq = t.UpdatedSeq,
            })],
            Threads = [.. threads.Select(th => new CollabThreadDocument
            {
                ThreadId = th.ThreadId,
                Title = th.Title,
                Initiator = th.InitiatorItemId,
                Participants = [.. th.ParticipantItemIds],
                Hops = th.Hops,
                MaxHops = th.MaxHops,
                Status = th.StatusKey,
                Updated = th.UpdatedAt,
                Seq = th.UpdatedSeq,
                Messages = [.. th.Messages.Skip(Math.Max(0, th.Messages.Count - MaxPersistedMessagesPerThread)).Select(m => new CollabMessageDocument
                {
                    Id = m.MessageId,
                    From = m.FromItemId,
                    FromLabel = m.FromLabel,
                    FromAgent = m.FromAgentId,
                    To = m.ToItemId,
                    ToLabel = m.ToLabel,
                    Round = m.Round,
                    Text = m.Text,
                    Status = m.Status,
                    User = m.IsUserInterjection,
                    At = m.Timestamp,
                })],
            })],
            Seen = Seen
                .Select(kv => (kv.Key, Entries: kv.Value.Where(e => kept.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal)))
                .Where(x => x.Entries.Count > 0)
                .ToDictionary(x => x.Key, x => x.Entries, StringComparer.Ordinal),
        };
    }

    private void FromDocument(CollabLedgerDocument doc)
    {
        _idSeq = Math.Max(_idSeq, doc.IdSeq);
        _eventSeq = Math.Max(_eventSeq, doc.EventSeq);

        foreach (var t in doc.Tasks)
        {
            if (t.TaskId.Length == 0) continue;
            var task = new CollabTaskRecord
            {
                TaskId = t.TaskId,
                CallerItemId = t.Caller,
                TargetItemId = t.Target,
                TargetAgentId = t.Agent,
                Title = t.Title ?? t.TaskId,
                Goal = t.Goal,
                Isolation = CollabCoordinator.ParseIsolation(t.Isolation) ?? CollabIsolationMode.Shared,
                WorktreeBranch = t.WorktreeBranch,
                WorktreePath = t.WorktreePath,
                ThreadId = t.ThreadId,
                Status = Enum.TryParse<CollabTaskStatus>(t.Status, out var status) ? status : CollabTaskStatus.Cancelled,
                LatestActivity = t.Activity,
                ResultSummary = t.Result,
                CreatedAt = t.Created ?? DateTimeOffset.UtcNow,
                CompletedAt = t.Completed,
                UpdatedSeq = t.Seq,
            };
            Tasks[task.TaskId] = task;
            if (!task.IsFinished)
            {
                SetTaskStatus(task, CollabTaskStatus.Cancelled, "Interrupted by restart",
                    "Octopus restarted before this task finished; the delegate's turn was not resumed. Delegate it again if it is still needed.");
            }
        }

        foreach (var th in doc.Threads)
        {
            if (th.ThreadId.Length == 0) continue;
            var thread = new CollabThreadRecord
            {
                ThreadId = th.ThreadId,
                Title = th.Title ?? th.ThreadId,
                InitiatorItemId = th.Initiator,
                ParticipantItemIds = [.. th.Participants],
                Hops = th.Hops,
                MaxHops = th.MaxHops ?? CollabSettings.DefaultMaxNegotiationHops,
                Status = CollabThreadStatusKeys.Parse(th.Status),
                UpdatedAt = th.Updated ?? DateTimeOffset.UtcNow,
                UpdatedSeq = th.Seq,
                Messages = [.. th.Messages.Select(m => new CollabMessageRecord
                {
                    MessageId = m.Id,
                    ThreadId = th.ThreadId,
                    FromItemId = m.From,
                    FromLabel = m.FromLabel,
                    FromAgentId = m.FromAgent,
                    ToItemId = m.To,
                    ToLabel = m.ToLabel,
                    Round = m.Round,
                    Text = m.Text,
                    Status = m.Status ?? "continue",
                    IsUserInterjection = m.User,
                    Timestamp = m.At ?? DateTimeOffset.UtcNow,
                })],
            };
            if (Threads.TryGetValue(thread.ThreadId, out var existing))
            {
                UnindexThreadLocked(existing);
            }
            Threads[thread.ThreadId] = thread;
            IndexThreadLocked(thread);
            if (thread.Status == CollabThreadStatus.Active) SetThreadStatus(thread, CollabThreadStatus.Interrupted);
        }

        foreach (var (caller, entries) in doc.Seen)
        {
            Seen[caller] = new Dictionary<string, long>(entries, StringComparer.Ordinal);
        }
    }

    public void Dispose()
    {
        Timer? timer;
        lock (Gate)
        {
            if (_disposed) return;
            _disposed = true;
            timer = _persistTimer;
            _persistTimer = null;
        }

        if (timer is not null)
        {
            using var timerDisposed = new ManualResetEvent(false);
            if (timer.Dispose(timerDisposed))
            {
                timerDisposed.WaitOne();
            }
        }

        FlushCore(isDisposing: true);
    }
}
