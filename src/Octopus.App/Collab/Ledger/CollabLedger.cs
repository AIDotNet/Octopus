using System.Text.Json;

using Octopus.Core.Agents;

namespace Octopus.App.Collab.Ledger;

/// <summary>A session owes <see cref="CallerItemId"/> an answer to <see cref="TaskId"/> / <see cref="ThreadId"/>.</summary>
internal sealed record PendingExpectation(
    string CallerItemId,
    string? TaskId,
    string? ThreadId,
    DateTimeOffset DispatchedAt);

/// <summary>An await_events call blocked until something changes.</summary>
internal sealed record EventWaiter(string CallerItemId, HashSet<string> FilterIds, TaskCompletionSource<bool> Tcs)
{
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
    private const int MaxPersistedTasks = 500;
    private const int MaxPersistedThreads = 300;
    private const int MaxPersistedMessagesPerThread = 40;
    private static readonly TimeSpan PersistRetention = TimeSpan.FromDays(14);

    private static readonly CollabLedgerJsonContext Json = new(new JsonSerializerOptions
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private readonly CollabStateStore? _store;
    private Timer? _persistTimer;
    private int _idSeq;
    private long _eventSeq;
    private bool _dirty;
    private bool _disposed;

    public CollabLedger(CollabStateStore? store) => _store = store;

    public object Gate { get; } = new();
    public Dictionary<string, CollabTaskRecord> Tasks { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, CollabThreadRecord> Threads { get; } = new(StringComparer.Ordinal);
    /// <summary>Open permission cards raised by children, keyed by child + card: a child (a remote one especially) may have several.</summary>
    public Dictionary<(string Child, string Approval), CollabBubbledApproval> Approvals { get; } = [];
    /// <summary>Per session: the answer it owes (the implicit reply when its turn ends without reply_thread).</summary>
    public Dictionary<string, PendingExpectation> Pending { get; } = new(StringComparer.Ordinal);
    public List<EventWaiter> Waiters { get; } = [];
    /// <summary>Per caller: task/thread id → the sequence number await_events last reported.</summary>
    public Dictionary<string, Dictionary<string, long>> Seen { get; } = new(StringComparer.Ordinal);

    /// <summary>A new id: <c>prefix_n_xxxx</c>, n counting up across restarts.</summary>
    public string NextId(string prefix) => $"{prefix}_{Interlocked.Increment(ref _idSeq)}_{Guid.NewGuid().ToString("N")[..4]}";

    /* ---------- transitions (hold Gate) ---------- */

    public void Add(CollabTaskRecord task)
    {
        Tasks[task.TaskId] = task;
        Touch(task);
    }

    public void Add(CollabThreadRecord thread)
    {
        Threads[thread.ThreadId] = thread;
        Touch(thread);
    }

    /// <summary>
    /// Moves a task to <paramref name="status"/>. <paramref name="activity"/> / <paramref name="result"/> replace the
    /// current ones when given. A finished task gets its completion time; a reopened one loses it.
    /// </summary>
    public void SetTaskStatus(CollabTaskRecord task, CollabTaskStatus status, string? activity = null, string? result = null)
    {
        task.Status = status;
        if (activity is not null) task.LatestActivity = activity;
        if (result is not null) task.ResultSummary = result;
        task.CompletedAt = task.IsFinished ? DateTimeOffset.UtcNow : null;
        Touch(task);
    }

    public void SetThreadStatus(CollabThreadRecord thread, CollabThreadStatus status)
    {
        thread.Status = status;
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
        _dirty = true;
    }

    public void Touch(CollabThreadRecord thread)
    {
        thread.UpdatedSeq = ++_eventSeq;
        thread.UpdatedAt = DateTimeOffset.UtcNow;
        _dirty = true;
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
        _dirty = true;
    }

    /* ---------- waiters (any thread) ---------- */

    public int WaiterCount
    {
        get
        {
            lock (Gate) return Waiters.Count;
        }
    }

    /// <summary>Wakes every blocked await_events so it looks again.</summary>
    public void SignalWaiters()
    {
        List<EventWaiter> copy;
        lock (Gate)
        {
            if (Waiters.Count == 0) return;
            copy = [.. Waiters];
        }
        foreach (var w in copy) w.Tcs.TrySetResult(true);
    }

    /* ---------- persistence ---------- */

    /// <summary>Saves soon (debounced) when something changed.</summary>
    public void SchedulePersist()
    {
        if (_store is null || _disposed) return;
        lock (Gate)
        {
            if (!_dirty) return;
            _persistTimer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _persistTimer.Change(PersistDebounceMs, Timeout.Infinite);
        }
    }

    public void Flush()
    {
        if (_store is null) return;
        string json;
        lock (Gate)
        {
            if (!_dirty) return;
            _dirty = false;
            json = JsonSerializer.Serialize(ToDocument(), Json.CollabLedgerDocument);
        }
        _store.Save(json);
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
            Threads[thread.ThreadId] = thread;
            if (thread.Status == CollabThreadStatus.Active) SetThreadStatus(thread, CollabThreadStatus.Interrupted);
        }

        foreach (var (caller, entries) in doc.Seen)
        {
            Seen[caller] = new Dictionary<string, long>(entries, StringComparer.Ordinal);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _persistTimer?.Dispose();
        Flush();
    }
}
