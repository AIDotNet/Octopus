using System.Text;
using Octopus.Remote.Protocol;

namespace Octopus.App.Collab;

/// <summary>
/// A GUI conversation on a paired host, followed here as a collaboration peer. It reads the host's flattened timeline
/// (<c>acp/attach</c> + <c>acp/update</c>) to tell when a turn ended and with what answer, raises the host's permission
/// cards on the delegating session, and sends prompts, cancels and answers back. The conversation itself lives on the host.
/// </summary>
internal sealed class RemoteCollabSession : ICollabSessionBridge, IDisposable
{
    private const int SummaryChars = 1200;
    private const int PartialCancelSummaryChars = 1500;
    private const int MaxReattachAttempts = 3;
    private const int ReattachBaseDelayMs = 50;
    private const int MaxBufferedUpdates = 256;
    private const int MaxRetainedEntries = 256;
    private static readonly TimeSpan ReattachAttemptTimeout = TimeSpan.FromSeconds(10);

    private readonly ICollabRemoteHost _host;
    private readonly ICollabCoordinator _coordinator;
    private readonly string _hostName;
    private readonly string? _parentItemId;
    private readonly bool _ownedByInitialLaunch;
    private string? _initialThreadId;
    private string? _initialTaskId;
    private string? _initialDeliveryId;
    private readonly Action<RemoteCollabSession> _closed;
    private readonly object _gate = new();
    private readonly List<AcpEntryMessage> _entries = [];
    private readonly List<AcpUpdateMessage> _early = [];
    private readonly List<TrackedDelivery> _trackedDeliveries = [];
    private readonly HashSet<string> _bubbled = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedAssistantIds = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposeCts = new();
    private long _sequence;
    private long _deliveryEpoch;
    private long _truncatedSnapshotAttachEpoch;
    private int _queuedCount;
    private string _state = AcpStates.Connecting;
    private string? _statusDetail;
    private string? _errorText;
    private bool _fresh;
    private bool _freshLaunchPending;
    private bool _activated;
    private bool _hasSnapshot;
    private bool _earlyOverflow;
    private long _earlyMaxDroppedSequence;
    private bool _reattaching;
    private int _consecutiveGapResets;
    private bool _errorReported;
    private bool _terminated;
    private bool _disposed;

    /// <param name="fresh">True for a session just started for a delegated task: an answer already in its first snapshot
    /// is that task's answer. False for an existing session: what it said before is not a reply to us.</param>
    public RemoteCollabSession(
        ICollabRemoteHost host,
        string hostName,
        string itemId,
        string agentId,
        string title,
        string workingDirectory,
        string? parentItemId,
        bool fresh,
        ICollabCoordinator coordinator,
        Action<RemoteCollabSession> closed,
        string? initialThreadId = null,
        string? initialTaskId = null,
        string? initialDeliveryId = null)
    {
        _host = host;
        _hostName = hostName;
        ItemId = itemId;
        AgentId = agentId;
        DisplayTitle = title;
        WorkingDirectory = workingDirectory;
        _parentItemId = parentItemId;
        _ownedByInitialLaunch = fresh;
        _fresh = fresh;
        _freshLaunchPending = fresh;
        _initialThreadId = initialThreadId;
        _initialTaskId = initialTaskId;
        _initialDeliveryId = initialDeliveryId;
        if (fresh && (!string.IsNullOrWhiteSpace(initialThreadId) || !string.IsNullOrWhiteSpace(initialTaskId) || !string.IsNullOrWhiteSpace(initialDeliveryId)))
        {
            _deliveryEpoch = 1;
            _trackedDeliveries.Add(new TrackedDelivery(initialThreadId, initialTaskId, initialDeliveryId, dispatchedEpoch: 1));
        }
        _coordinator = coordinator;
        _closed = closed;
    }

    public string ItemId { get; }
    public string AgentId { get; }
    public string DisplayTitle { get; }
    public string WorkingDirectory { get; }
    public string HostName => _hostName;
    internal bool SupportsScopedCancel => _host.SupportsScopedCancel;

    internal bool IsActivated
    {
        get
        {
            lock (_gate) return _activated;
        }
    }

    internal bool IsAlive
    {
        get
        {
            lock (_gate) return !_disposed && !_terminated;
        }
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate) return _state is AcpStates.Working or AcpStates.Waiting;
        }
    }

    public bool IsConnected
    {
        get
        {
            lock (_gate) return !_disposed && !_terminated && !_host.Completion.IsCompleted && _state != AcpStates.Error;
        }
    }

    public string? CurrentStatusDetail
    {
        get
        {
            lock (_gate) return _statusDetail is { Length: > 0 } detail ? $"{detail} (on {_hostName})" : $"on {_hostName}";
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        long attachEpoch;
        lock (_gate)
        {
            attachEpoch = _deliveryEpoch;
        }
        _host.AcpUpdated += OnUpdate;
        AcpSnapshotMessage snapshot;
        try
        {
            snapshot = await _host.AttachConversationAsync(ItemId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var keepAliveForFollowUp = false;
            lock (_gate)
            {
                var hasOtherAccepted = _ownedByInitialLaunch
                    ? HasFollowUpDeliveryLocked()
                    : _trackedDeliveries.Count > 0;
                if (!_disposed && !_terminated && hasOtherAccepted)
                {
                    _activated = true;
                    if (_ownedByInitialLaunch)
                    {
                        _freshLaunchPending = false;
                    }
                    keepAliveForFollowUp = true;
                }
            }
            if (keepAliveForFollowUp)
            {
                _ = _host.Completion.ContinueWith(_ => OnDisconnected(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                ScheduleReattach();
            }
            else
            {
                Dispose();
            }
            throw;
        }
        catch (Exception e)
        {
            var settleOtherAccepted = false;
            HashSet<string>? onlyDeliveryIds = null;
            lock (_gate)
            {
                if (!_disposed && !_terminated)
                {
                    var otherDeliveries = CollectOtherAcceptedDeliveriesLocked();
                    if (otherDeliveries.Count > 0)
                    {
                        settleOtherAccepted = true;
                        if (_ownedByInitialLaunch && otherDeliveries.All(d => !string.IsNullOrWhiteSpace(d.DeliveryId)))
                        {
                            onlyDeliveryIds = new HashSet<string>(otherDeliveries.Select(d => d.DeliveryId!), StringComparer.Ordinal);
                        }
                    }
                }
            }
            if (settleOtherAccepted)
            {
                TerminateWithFailure(FormatAttachFailure(e), onlyDeliveryIds);
            }
            Dispose();
            throw;
        }

        bool markExistingAnswerSeen;
        lock (_gate)
        {
            if (_disposed || _terminated) return;
            _activated = true;
            markExistingAnswerSeen = !_fresh;
        }
        _ = _host.Completion.ContinueWith(_ => OnDisconnected(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var needReattach = ApplySnapshotAndReplay(snapshot, markExistingAnswerSeen, attachEpoch);
        if (needReattach)
        {
            ScheduleReattach();
            return;
        }
        Evaluate();
    }

    internal bool TryAttachInitialLaunch(string? threadId, string? taskId, string? deliveryId)
    {
        var shouldEvaluate = false;
        lock (_gate)
        {
            if (_disposed || _terminated) return false;
            _fresh = true;
            _initialThreadId = threadId;
            _initialTaskId = taskId;
            _initialDeliveryId = deliveryId;
            if (!string.IsNullOrWhiteSpace(threadId) || !string.IsNullOrWhiteSpace(taskId) || !string.IsNullOrWhiteSpace(deliveryId))
            {
                var epoch = _deliveryEpoch == 0 ? ++_deliveryEpoch : _deliveryEpoch;
                if (!_trackedDeliveries.Any(t => MatchesTrackedDelivery(t, threadId, taskId, deliveryId)))
                {
                    _trackedDeliveries.Add(new TrackedDelivery(threadId, taskId, deliveryId, epoch));
                }
            }
            if (!_hasSnapshot)
            {
                _freshLaunchPending = true;
                return true;
            }
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Kind != AcpEntryKinds.Assistant) continue;
                var userIndex = FindPrecedingUserIndexLocked(i);
                var userEntry = userIndex >= 0 ? _entries[userIndex] : null;
                if (ResolveEntryCorrelation(entry, userEntry) is { ExpectsReply: true } resolved
                    && MatchesInitialLaunch(resolved.ThreadId, resolved.TaskId, resolved.DeliveryId)
                    && _reportedAssistantIds.Remove(entry.Id)
                    && !entry.IsStreaming)
                {
                    shouldEvaluate = true;
                }
            }
        }
        if (shouldEvaluate) Evaluate();
        return true;
    }

    private List<TrackedDelivery> CollectOtherAcceptedDeliveriesLocked()
    {
        List<TrackedDelivery> others = [];
        foreach (var tracked in _trackedDeliveries)
        {
            if (!_ownedByInitialLaunch || !MatchesInitialLaunch(tracked.ThreadId, tracked.TaskId, tracked.DeliveryId))
            {
                others.Add(tracked);
            }
        }
        return others;
    }

    private bool HasFollowUpDeliveryLocked()
    {
        foreach (var tracked in _trackedDeliveries)
        {
            if (!MatchesInitialLaunch(tracked.ThreadId, tracked.TaskId, tracked.DeliveryId))
            {
                return true;
            }
        }
        return false;
    }

    /* ---------- ICollabSessionBridge ---------- */

    public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default) =>
        DeliverCollabPromptAsync(promptText, threadId, taskId, expectReply: threadId is not null || taskId is not null, deliveryId: null, cancellationToken);

    public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId, string? taskId, bool expectReply, CancellationToken cancellationToken) =>
        DeliverCollabPromptAsync(promptText, threadId, taskId, expectReply, deliveryId: null, cancellationToken);

    public async Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId, string? taskId, bool expectReply, string? deliveryId, CancellationToken cancellationToken)
    {
        TrackedDelivery? tracked = null;
        lock (_gate)
        {
            if (_disposed || _terminated) return false;
            if (expectReply && (!string.IsNullOrWhiteSpace(threadId) || !string.IsNullOrWhiteSpace(taskId) || !string.IsNullOrWhiteSpace(deliveryId)))
            {
                // Keep epoch above any in-flight snapshot attach until SendPromptAsync finishes on the host.
                tracked = new TrackedDelivery(threadId, taskId, deliveryId, long.MaxValue);
                _trackedDeliveries.Add(tracked);
            }
        }
        try
        {
            await _host.SendPromptAsync(ItemId, promptText, expectReply ? deliveryId : null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or RemoteException or InvalidOperationException or ObjectDisposedException)
        {
            RemoveTrackedDelivery(tracked);
            return false;
        }
        catch
        {
            RemoveTrackedDelivery(tracked);
            throw;
        }

        if (tracked is not null)
        {
            lock (_gate)
            {
                if (!_disposed && !_terminated && _trackedDeliveries.Contains(tracked))
                {
                    tracked.DispatchedEpoch = ++_deliveryEpoch;
                }
            }
        }
        return true;
    }

    // The host queues prompts itself and offers no way to withdraw one, so nothing can be taken back here.
    public int CancelQueuedCollabPrompts(string? threadId = null, string? taskId = null) => 0;

    public Task CancelActiveTurnAsync()
    {
        lock (_gate)
        {
            if (_disposed || _terminated) return Task.CompletedTask;
            _freshLaunchPending = false;
        }
        return SendCancelTurnAsync();
    }

    public Task CancelActiveTurnAsync(string? threadId, string? taskId) =>
        CancelActiveTurnAsync(threadId, taskId, deliveryId: null);

    public Task CancelActiveTurnAsync(string? threadId, string? taskId, string? deliveryId)
    {
        var hasIdentity = !string.IsNullOrWhiteSpace(threadId)
            || !string.IsNullOrWhiteSpace(taskId)
            || !string.IsNullOrWhiteSpace(deliveryId);
        if (!hasIdentity)
        {
            return CancelActiveTurnAsync();
        }

        var supportsScopedCancel = _host.SupportsScopedCancel;
        bool shouldCancelHost;
        lock (_gate)
        {
            if (_disposed || _terminated) return Task.CompletedTask;
            var matchesObservedActive = supportsScopedCancel && CanCancelActiveRemoteTurnLocked(threadId, taskId, deliveryId);
            var wasFreshPending = _freshLaunchPending && MatchesInitialLaunch(threadId, taskId, deliveryId);
            if (wasFreshPending)
            {
                _freshLaunchPending = false;
            }
            var removedTracked = RemoveMatchingTrackedDeliveryLocked(threadId, taskId, deliveryId);
            // Only hosts advertising acp.collabCancel can verify turn ownership on the host at cancellation time.
            // Without that capability, an observed active turn on the mirror cannot prove the host has not already
            // moved on to a later human turn, so task-directed cancellation clears local tracking and declines the
            // unscoped host interrupt (explicit human Stop via 0-arg CancelActiveTurnAsync remains unchanged).
            shouldCancelHost = supportsScopedCancel && (matchesObservedActive || removedTracked || wasFreshPending);
        }
        return shouldCancelHost ? SendScopedCancelTurnAsync(threadId, taskId, deliveryId) : Task.CompletedTask;
    }

    private async Task SendCancelTurnAsync()
    {
        try { await _host.CancelTurnAsync(ItemId, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or RemoteException or InvalidOperationException or ObjectDisposedException) { }
    }

    private async Task SendScopedCancelTurnAsync(string? threadId, string? taskId, string? deliveryId)
    {
        try { await _host.CancelTurnAsync(ItemId, threadId, taskId, deliveryId, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or RemoteException or InvalidOperationException or ObjectDisposedException) { }
    }

    public string ReadRecentSummary(int maxTurns = 5)
    {
        List<AcpEntryMessage> recent;
        lock (_gate)
        {
            recent = _entries.Where(e => e.Kind is AcpEntryKinds.User or AcpEntryKinds.Assistant).TakeLast(Math.Max(1, maxTurns) * 2).ToList();
        }
        if (recent.Count == 0) return $"(No turns yet in this session on {_hostName}.)";
        var sb = new StringBuilder();
        foreach (var entry in recent)
        {
            var text = entry.Kind == AcpEntryKinds.User ? entry.Text : entry.Text ?? entry.ErrorText;
            if (string.IsNullOrWhiteSpace(text)) continue;
            text = text.Trim();
            if (text.Length > SummaryChars) text = text[..SummaryChars] + "…";
            sb.Append(entry.Kind == AcpEntryKinds.User ? "User: " : "Assistant: ").AppendLine(text).AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    /* ---------- following the host's timeline ---------- */

    private void OnUpdate(AcpUpdateMessage update)
    {
        if (update.ItemId != ItemId) return;
        var shouldEvaluate = false;
        var startReattach = false;
        lock (_gate)
        {
            if (_disposed || _terminated) return;
            if (!_hasSnapshot)
            {
                BufferEarlyLocked(update);
                return;
            }
            if (update.Sequence <= _sequence) return;
            if (update.Sequence > _sequence + 1)
            {
                // Missed one or more updates: discard stale pre-gap state and request a fresh snapshot.
                _hasSnapshot = false;
                ResetEarlyBufferLocked();
                if (!_reattaching)
                {
                    _reattaching = true;
                    startReattach = true;
                }
            }
            else
            {
                ApplyUpdateLocked(update);
                _consecutiveGapResets = 0;
                shouldEvaluate = true;
            }
        }
        if (startReattach) RunReattachLoop();
        else if (shouldEvaluate) Evaluate();
    }

    private void BufferEarlyLocked(AcpUpdateMessage update)
    {
        if (_early.Count < MaxBufferedUpdates)
        {
            _early.Add(update);
            return;
        }
        _earlyOverflow = true;
        if (update.Sequence > _earlyMaxDroppedSequence)
        {
            _earlyMaxDroppedSequence = update.Sequence;
        }
    }

    private void ResetEarlyBufferLocked()
    {
        _early.Clear();
        _earlyOverflow = false;
        _earlyMaxDroppedSequence = 0;
    }

    private void ApplyUpdateLocked(AcpUpdateMessage update)
    {
        _sequence = update.Sequence;
        foreach (var id in update.Removed)
        {
            _entries.RemoveAll(e => e.Id == id);
            _reportedAssistantIds.Remove(id);
        }
        foreach (var entry in update.Upserts)
        {
            var index = _entries.FindIndex(e => e.Id == entry.Id);
            if (index >= 0)
            {
                _entries[index] = entry;
                continue;
            }
            var after = entry.AfterId is null ? -1 : _entries.FindIndex(e => e.Id == entry.AfterId);
            if (after >= 0) _entries.Insert(after + 1, entry);
            else _entries.Add(entry);
        }
        if (update.State is { } state)
        {
            if (update.StatusDetail is null) _statusDetail = null;
            if (update.ErrorText is null && state != AcpStates.Error) _errorText = null;
            _state = state;
        }
        if (update.StatusDetail is not null) _statusDetail = NullIfBlank(update.StatusDetail);
        if (update.ErrorText is not null) _errorText = NullIfBlank(update.ErrorText);
        if (update.QueuedCount is { } queuedCount) _queuedCount = Math.Max(0, queuedCount);
    }

    private bool ApplySnapshotAndReplay(AcpSnapshotMessage snapshot, bool markExistingAnswerSeen, long attachEpoch)
    {
        lock (_gate)
        {
            if (_disposed || _terminated) return false;
            return ApplySnapshotAndReplayLocked(snapshot, markExistingAnswerSeen, attachEpoch);
        }
    }

    private bool ApplySnapshotAndReplayLocked(AcpSnapshotMessage snapshot, bool markExistingAnswerSeen, long attachEpoch)
    {
        _sequence = snapshot.Sequence;
        _hasSnapshot = true;
        _entries.Clear();
        _entries.AddRange(snapshot.Entries);
        PruneReportedAssistantsToLiveEntriesLocked();
        _state = snapshot.State;
        _statusDetail = NullIfBlank(snapshot.StatusDetail);
        _errorText = NullIfBlank(snapshot.ErrorText);
        _queuedCount = Math.Max(0, snapshot.QueuedCount);
        if (snapshot.HasEarlier)
        {
            // An incomplete snapshot cannot safely attribute an uncorrelated turn to the original launch delivery.
            _freshLaunchPending = false;
            if (attachEpoch > _truncatedSnapshotAttachEpoch)
            {
                _truncatedSnapshotAttachEpoch = attachEpoch;
            }
        }
        if (markExistingAnswerSeen)
        {
            MarkCurrentAssistantsSeenLocked();
        }

        var overflowed = _earlyOverflow;
        var maxDroppedSequence = _earlyMaxDroppedSequence;
        var buffered = _early.Count > 0
            ? _early.Where(u => u.Sequence > _sequence).OrderBy(u => u.Sequence).ToList()
            : null;
        ResetEarlyBufferLocked();

        if (buffered is not null)
        {
            foreach (var update in buffered)
            {
                if (update.Sequence <= _sequence) continue;
                if (update.Sequence > _sequence + 1)
                {
                    _hasSnapshot = false;
                    return true;
                }
                ApplyUpdateLocked(update);
            }
        }

        if (overflowed && maxDroppedSequence > _sequence)
        {
            _hasSnapshot = false;
            return true;
        }
        return false;
    }

    private void PruneReportedAssistantsToLiveEntriesLocked()
    {
        if (_reportedAssistantIds.Count == 0) return;
        if (_entries.Count == 0)
        {
            _reportedAssistantIds.Clear();
            return;
        }
        var liveIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in _entries)
        {
            if (entry.Kind == AcpEntryKinds.Assistant)
            {
                liveIds.Add(entry.Id);
            }
        }
        _reportedAssistantIds.IntersectWith(liveIds);
    }

    private void TrimRetainedEntriesLocked()
    {
        if (_entries.Count <= MaxRetainedEntries) return;
        var excess = _entries.Count - MaxRetainedEntries;
        var index = 0;
        while (excess > 0 && index < _entries.Count)
        {
            var entry = _entries[index];
            if (IsProtectedFromTrimLocked(index, entry))
            {
                index++;
                continue;
            }
            _entries.RemoveAt(index);
            if (entry.Kind == AcpEntryKinds.Assistant)
            {
                _reportedAssistantIds.Remove(entry.Id);
            }
            excess--;
        }
    }

    private bool IsProtectedFromTrimLocked(int index, AcpEntryMessage entry)
    {
        if (entry.Permission is { IsResolved: false } || entry.Question is { IsResolved: false } || _bubbled.Contains(entry.Id))
        {
            return true;
        }
        if (entry.Kind == AcpEntryKinds.Assistant)
        {
            return entry.IsStreaming || !_reportedAssistantIds.Contains(entry.Id);
        }
        if (entry.Kind == AcpEntryKinds.User)
        {
            for (var j = index + 1; j < _entries.Count; j++)
            {
                if (_entries[j].Kind == AcpEntryKinds.User)
                {
                    return false;
                }
                if (_entries[j].Kind == AcpEntryKinds.Assistant)
                {
                    return _entries[j].IsStreaming || !_reportedAssistantIds.Contains(_entries[j].Id);
                }
            }
            return true;
        }
        return false;
    }

    private void MarkCurrentAssistantsSeenLocked()
    {
        foreach (var entry in _entries)
        {
            if (entry.Kind == AcpEntryKinds.Assistant)
            {
                _reportedAssistantIds.Add(entry.Id);
            }
        }
    }

    private void ScheduleReattach()
    {
        lock (_gate)
        {
            if (_disposed || _terminated || _reattaching) return;
            _reattaching = true;
        }
        RunReattachLoop();
    }

    private void RunReattachLoop()
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 1; attempt <= MaxReattachAttempts; attempt++)
            {
                long attachEpoch;
                lock (_gate)
                {
                    if (_disposed || _terminated) return;
                    attachEpoch = _deliveryEpoch;
                }
                if (_host.Completion.IsCompleted)
                {
                    OnDisconnected();
                    return;
                }

                try
                {
                    using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
                    attemptCts.CancelAfter(ReattachAttemptTimeout);
                    var snapshot = await _host.AttachConversationAsync(ItemId, attemptCts.Token).ConfigureAwait(false);

                    bool hitGap;
                    var exhaustedGapResets = false;
                    lock (_gate)
                    {
                        if (_disposed || _terminated) return;
                        hitGap = ApplySnapshotAndReplayLocked(snapshot, markExistingAnswerSeen: false, attachEpoch);
                        if (!hitGap)
                        {
                            _reattaching = false;
                            _consecutiveGapResets = 0;
                        }
                        else
                        {
                            ResetEarlyBufferLocked();
                            _consecutiveGapResets++;
                            if (_consecutiveGapResets >= MaxReattachAttempts || attempt >= MaxReattachAttempts)
                            {
                                exhaustedGapResets = true;
                            }
                        }
                    }

                    if (!hitGap)
                    {
                        Evaluate();
                        return;
                    }
                    if (exhaustedGapResets)
                    {
                        TerminateWithFailure($"Lost synchronization with {DisplayTitle} on {_hostName} after repeated sequence gaps; the agent may still be running there. Check the session on that host.");
                        return;
                    }
                }
                catch (Exception e)
                {
                    lock (_gate)
                    {
                        if (_disposed || _terminated) return;
                    }
                    if (_host.Completion.IsCompleted)
                    {
                        OnDisconnected();
                        return;
                    }

                    var isTransient = IsTransientReattachFailure(e);
                    if (!isTransient || attempt >= MaxReattachAttempts)
                    {
                        TerminateWithFailure(FormatReattachFailure(e));
                        return;
                    }

                    try
                    {
                        await Task.Delay(ReattachBaseDelayMs * attempt, _disposeCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        });
    }

    private bool IsTransientReattachFailure(Exception e) => e switch
    {
        IOException => true,
        TimeoutException => true,
        OperationCanceledException => !_disposeCts.IsCancellationRequested,
        RemoteException re => re.Code == RemoteProtocol.InternalError,
        _ => false,
    };

    private string FormatReattachFailure(Exception e)
    {
        if (e is RemoteException { Code: RemoteMethods.NotFound } notFound)
        {
            return $"{DisplayTitle} on {_hostName} could not be followed after a sync gap ({notFound.Message}). Check the session on that host.";
        }
        return $"Lost synchronization with {DisplayTitle} on {_hostName} and could not reattach ({e.Message}); the agent may still be running there. Check the session on that host.";
    }

    private sealed class TrackedDelivery(string? threadId, string? taskId, string? deliveryId, long dispatchedEpoch)
    {
        public string? ThreadId { get; } = threadId;
        public string? TaskId { get; } = taskId;
        public string? DeliveryId { get; } = deliveryId;
        public long DispatchedEpoch { get; set; } = dispatchedEpoch;
    }

    private readonly record struct RemoteTurnOutcome(
        string? Answer,
        string? AbortReason,
        bool Failed,
        string? ThreadId,
        string? TaskId,
        string? DeliveryId,
        bool HasTurnIdentity,
        bool Deferred);

    private bool MatchesInitialLaunch(string? threadId, string? taskId, string? deliveryId)
    {
        if (string.IsNullOrWhiteSpace(threadId) && string.IsNullOrWhiteSpace(taskId) && string.IsNullOrWhiteSpace(deliveryId))
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(_initialDeliveryId) && !string.IsNullOrWhiteSpace(deliveryId))
        {
            return string.Equals(_initialDeliveryId, deliveryId, StringComparison.Ordinal);
        }
        var matchesTask = !string.IsNullOrWhiteSpace(_initialTaskId)
            && !string.IsNullOrWhiteSpace(taskId)
            && string.Equals(_initialTaskId, taskId, StringComparison.Ordinal);
        var matchesThread = !string.IsNullOrWhiteSpace(_initialThreadId)
            && !string.IsNullOrWhiteSpace(threadId)
            && string.Equals(_initialThreadId, threadId, StringComparison.Ordinal);
        return matchesTask || matchesThread;
    }

    private readonly record struct ResolvedCorrelation(string? ThreadId, string? TaskId, string? DeliveryId, bool ExpectsReply, bool Deferred);

    private static ResolvedCorrelation? ResolveEntryCorrelation(AcpEntryMessage assistant, AcpEntryMessage? user)
    {
        if (assistant.Collab is { } assistantCollab)
        {
            return new ResolvedCorrelation(assistantCollab.ThreadId, assistantCollab.TaskId, assistantCollab.DeliveryId, assistantCollab.ExpectsReply, assistantCollab.Deferred);
        }
        if (user?.Collab is { } userCollab)
        {
            return new ResolvedCorrelation(userCollab.ThreadId, userCollab.TaskId, userCollab.DeliveryId, userCollab.ExpectsReply, userCollab.Deferred);
        }
        if (user is not null && CollabEnvelope.TryExtractCorrelation(user.Text) is { } parsed)
        {
            return new ResolvedCorrelation(parsed.ThreadId, parsed.TaskId, DeliveryId: null, parsed.ExpectsReply, Deferred: false);
        }
        return null;
    }

    /// <summary>Turns the timeline's state into collaboration events: an answer, a failure, permission cards to raise or clear.</summary>
    private void Evaluate()
    {
        string? sessionFailure = null;
        List<RemoteTurnOutcome> outcomes = [];
        List<AcpEntryMessage> raise = [];
        List<string> clear = [];

        lock (_gate)
        {
            if (_disposed || _terminated) return;

            if (_state == AcpStates.Error)
            {
                if (!_errorReported)
                {
                    _errorReported = true;
                    sessionFailure = $"{DisplayTitle} on {_hostName} failed: {_errorText ?? "the agent could not be started or its connection broke."}";
                }
            }
            else
            {
                _errorReported = false;
            }

            // Scan finished assistant turns not yet reported. A turn is complete once its assistant entry stopped
            // streaming and either the conversation is Ready or a later user turn began.
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i] is not { Kind: AcpEntryKinds.Assistant, IsStreaming: false } finished) continue;
                if (_reportedAssistantIds.Contains(finished.Id)) continue;
                var userIndex = -1;
                for (var j = i - 1; j >= 0; j--)
                {
                    if (_entries[j].Kind == AcpEntryKinds.User)
                    {
                        userIndex = j;
                        break;
                    }
                    if (_entries[j].Kind == AcpEntryKinds.Assistant) break;
                }
                var userEntry = userIndex >= 0 ? _entries[userIndex] : null;
                var correlation = ResolveEntryCorrelation(finished, userEntry);
                if (userEntry is null && correlation is null) continue;

                var hasLaterUser = false;
                for (var j = i + 1; j < _entries.Count; j++)
                {
                    if (_entries[j].Kind == AcpEntryKinds.User)
                    {
                        hasLaterUser = true;
                        break;
                    }
                }
                if (_state != AcpStates.Ready && !hasLaterUser) continue;

                _reportedAssistantIds.Add(finished.Id);

                string? outcomeThreadId;
                string? outcomeTaskId;
                string? outcomeDeliveryId;
                bool hasTurnIdentity;
                bool outcomeDeferred;
                if (correlation is { } resolved)
                {
                    if (!resolved.ExpectsReply) continue;
                    if (_freshLaunchPending && MatchesInitialLaunch(resolved.ThreadId, resolved.TaskId, resolved.DeliveryId))
                    {
                        _freshLaunchPending = false;
                    }
                    outcomeThreadId = resolved.ThreadId;
                    outcomeTaskId = resolved.TaskId;
                    outcomeDeliveryId = resolved.DeliveryId;
                    hasTurnIdentity = true;
                    outcomeDeferred = resolved.Deferred;
                }
                else if (_freshLaunchPending && userEntry is not null && !HasEarlierUserLocked(userIndex))
                {
                    // One-shot compatibility fallback strictly tied to the original launch delivery in a complete history (never reused later).
                    _freshLaunchPending = false;
                    outcomeThreadId = _initialThreadId;
                    outcomeTaskId = _initialTaskId;
                    outcomeDeliveryId = _initialDeliveryId;
                    hasTurnIdentity = !string.IsNullOrWhiteSpace(_initialThreadId)
                        || !string.IsNullOrWhiteSpace(_initialTaskId)
                        || !string.IsNullOrWhiteSpace(_initialDeliveryId);
                    outcomeDeferred = false;
                }
                else
                {
                    continue;
                }

                // Clear from unobserved tracked deliveries once observed (including explicit Deferred handoffs, so
                // later manual turns, idle Ready states, snapshot trimming, and reconnect do not abort a deferred middle).
                RemoveMatchingTrackedDeliveryLocked(outcomeThreadId, outcomeTaskId, outcomeDeliveryId);

                string? answer = null;
                string? abortReason = null;
                var failed = false;
                if (!string.IsNullOrWhiteSpace(finished.ErrorText))
                {
                    abortReason = $"{DisplayTitle} on {_hostName} failed: {finished.ErrorText}";
                    failed = true;
                }
                else if (string.Equals(finished.StopReason, "cancelled", StringComparison.Ordinal))
                {
                    abortReason = FormatStoppedReason(finished.Text);
                    failed = false;
                }
                else if (!outcomeDeferred)
                {
                    answer = !string.IsNullOrWhiteSpace(finished.Text)
                        ? finished.Text
                        : "(The remote session finished its turn with no text output.)";
                }

                outcomes.Add(new RemoteTurnOutcome(answer, abortReason, failed, outcomeThreadId, outcomeTaskId, outcomeDeliveryId, hasTurnIdentity, outcomeDeferred));
            }

            if (sessionFailure is not null)
            {
                // Prevent incomplete pre-crash assistant entries from being replayed as completed if the host reconnects later.
                _freshLaunchPending = false;
                _trackedDeliveries.Clear();
                MarkCurrentAssistantsSeenLocked();
            }
            else if (_state == AcpStates.Ready
                && _queuedCount == 0
                && _truncatedSnapshotAttachEpoch > 0
                && _trackedDeliveries.Count > 0
                && !HasStreamingAssistantLocked())
            {
                // A truncated snapshot after dispatch left the host Ready with nothing queued and no entry for this
                // delivery: its result cannot be recovered from history, so expose uncertainty without re-executing.
                for (var i = 0; i < _trackedDeliveries.Count;)
                {
                    var tracked = _trackedDeliveries[i];
                    if (tracked.DispatchedEpoch > _truncatedSnapshotAttachEpoch || HasTrackedDeliveryEntryLocked(tracked))
                    {
                        i++;
                        continue;
                    }
                    _trackedDeliveries.RemoveAt(i);
                    if (_freshLaunchPending && MatchesInitialLaunch(tracked.ThreadId, tracked.TaskId, tracked.DeliveryId))
                    {
                        _freshLaunchPending = false;
                    }
                    outcomes.Add(new RemoteTurnOutcome(
                        Answer: null,
                        AbortReason: FormatEvictedDeliveryReason(),
                        Failed: true,
                        tracked.ThreadId,
                        tracked.TaskId,
                        tracked.DeliveryId,
                        HasTurnIdentity: true,
                        Deferred: false));
                }
            }

            if (_parentItemId is not null)
            {
                foreach (var entry in _entries)
                {
                    if (entry.Kind == AcpEntryKinds.Permission && entry.Permission is { IsResolved: false } && _bubbled.Add(entry.Id))
                    {
                        raise.Add(entry);
                    }
                }
                foreach (var id in _bubbled.ToList())
                {
                    var entry = _entries.Find(e => e.Id == id);
                    if (entry?.Permission is null or { IsResolved: true })
                    {
                        _bubbled.Remove(id);
                        clear.Add(id);
                    }
                }
            }

            TrimRetainedEntriesLocked();
        }

        foreach (var id in clear) _coordinator.ClearChildApproval(ItemId, id);
        foreach (var entry in raise) RaiseApproval(entry);
        foreach (var outcome in outcomes)
        {
            if (outcome.AbortReason is not null)
            {
                if (outcome.HasTurnIdentity) _coordinator.NotifyTurnAborted(ItemId, outcome.AbortReason, failed: outcome.Failed, outcome.ThreadId, outcome.TaskId, outcome.DeliveryId);
                else _coordinator.NotifyTurnAborted(ItemId, outcome.AbortReason, failed: outcome.Failed);
            }
            else if (outcome.Deferred)
            {
                _coordinator.NotifyTurnDeferred(ItemId, outcome.ThreadId, outcome.TaskId, outcome.DeliveryId);
            }
            else if (outcome.Answer is not null)
            {
                if (outcome.HasTurnIdentity) _coordinator.NotifyTurnCompleted(ItemId, outcome.Answer, outcome.ThreadId, outcome.TaskId, outcome.DeliveryId);
                else _coordinator.NotifyTurnCompleted(ItemId, outcome.Answer);
            }
        }
        if (sessionFailure is not null)
        {
            AbortAllPending(sessionFailure);
        }
    }

    private bool HasEarlierUserLocked(int userIndex)
    {
        for (var j = 0; j < userIndex; j++)
        {
            if (_entries[j].Kind == AcpEntryKinds.User)
            {
                return true;
            }
        }
        return false;
    }

    private bool HasStreamingAssistantLocked()
    {
        foreach (var entry in _entries)
        {
            if (entry.Kind == AcpEntryKinds.Assistant && entry.IsStreaming)
            {
                return true;
            }
        }
        return false;
    }

    private void RemoveTrackedDelivery(TrackedDelivery? tracked)
    {
        if (tracked is null) return;
        lock (_gate)
        {
            _trackedDeliveries.Remove(tracked);
        }
    }

    private bool RemoveMatchingTrackedDeliveryLocked(string? threadId, string? taskId, string? deliveryId)
    {
        if (_trackedDeliveries.Count == 0) return false;
        for (var i = 0; i < _trackedDeliveries.Count; i++)
        {
            if (MatchesTrackedDelivery(_trackedDeliveries[i], threadId, taskId, deliveryId))
            {
                _trackedDeliveries.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    private bool HasTrackedDeliveryEntryLocked(TrackedDelivery tracked)
    {
        foreach (var entry in _entries)
        {
            if (entry.Kind is not (AcpEntryKinds.User or AcpEntryKinds.Assistant)) continue;
            if (entry.Collab is { } collab)
            {
                if (collab.ExpectsReply && MatchesTrackedDelivery(tracked, collab.ThreadId, collab.TaskId, collab.DeliveryId))
                {
                    return true;
                }
                continue;
            }
            if (entry.Kind == AcpEntryKinds.User
                && CollabEnvelope.TryExtractCorrelation(entry.Text) is { ExpectsReply: true } parsed
                && MatchesTrackedDelivery(tracked, parsed.ThreadId, parsed.TaskId, deliveryId: null))
            {
                return true;
            }
        }
        return false;
    }

    private bool CanCancelActiveRemoteTurnLocked(string? threadId, string? taskId, string? deliveryId)
    {
        // Only send a session-wide acp/cancel when the observed active turn matches the cancelled delivery identity
        // (or is a fresh initial launch before its first entry arrives); never stop unrelated or ambiguous user work.
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            var entry = _entries[i];
            if (entry.Kind == AcpEntryKinds.Assistant)
            {
                if (!entry.IsStreaming)
                {
                    break;
                }
                var userIndex = FindPrecedingUserIndexLocked(i);
                var userEntry = userIndex >= 0 ? _entries[userIndex] : null;
                if (ResolveEntryCorrelation(entry, userEntry) is { } resolved)
                {
                    return resolved.ExpectsReply && MatchesIdentity(resolved.ThreadId, resolved.TaskId, resolved.DeliveryId, threadId, taskId, deliveryId);
                }
                return _freshLaunchPending && userEntry is not null && !HasEarlierUserLocked(userIndex) && MatchesInitialLaunch(threadId, taskId, deliveryId);
            }
            if (entry.Kind == AcpEntryKinds.User)
            {
                if (entry.Collab is { } userCollab)
                {
                    return userCollab.ExpectsReply && MatchesIdentity(userCollab.ThreadId, userCollab.TaskId, userCollab.DeliveryId, threadId, taskId, deliveryId);
                }
                if (CollabEnvelope.TryExtractCorrelation(entry.Text) is { } parsed)
                {
                    return parsed.ExpectsReply && MatchesIdentity(parsed.ThreadId, parsed.TaskId, candidateDeliveryId: null, threadId, taskId, deliveryId);
                }
                return _freshLaunchPending && !HasEarlierUserLocked(i) && MatchesInitialLaunch(threadId, taskId, deliveryId);
            }
        }

        return _entries.Count == 0
            && _state is AcpStates.Connecting or AcpStates.Working or AcpStates.Waiting
            && _freshLaunchPending
            && MatchesInitialLaunch(threadId, taskId, deliveryId);
    }

    private int FindPrecedingUserIndexLocked(int assistantIndex)
    {
        for (var j = assistantIndex - 1; j >= 0; j--)
        {
            if (_entries[j].Kind == AcpEntryKinds.User) return j;
            if (_entries[j].Kind == AcpEntryKinds.Assistant) break;
        }
        return -1;
    }

    private static bool MatchesTrackedDelivery(TrackedDelivery tracked, string? threadId, string? taskId, string? deliveryId) =>
        MatchesIdentity(tracked.ThreadId, tracked.TaskId, tracked.DeliveryId, threadId, taskId, deliveryId);

    private static bool MatchesIdentity(
        string? candidateThreadId,
        string? candidateTaskId,
        string? candidateDeliveryId,
        string? threadId,
        string? taskId,
        string? deliveryId)
    {
        if (!string.IsNullOrWhiteSpace(candidateDeliveryId) && !string.IsNullOrWhiteSpace(deliveryId))
        {
            return string.Equals(candidateDeliveryId, deliveryId, StringComparison.Ordinal);
        }
        var hasTask = !string.IsNullOrWhiteSpace(taskId);
        var hasThread = !string.IsNullOrWhiteSpace(threadId);
        if (hasTask && !string.IsNullOrWhiteSpace(candidateTaskId) && !string.Equals(candidateTaskId, taskId, StringComparison.Ordinal))
        {
            return false;
        }
        if (hasThread && !string.IsNullOrWhiteSpace(candidateThreadId) && !string.Equals(candidateThreadId, threadId, StringComparison.Ordinal))
        {
            return false;
        }
        if (hasTask && !string.IsNullOrWhiteSpace(candidateTaskId))
        {
            return true;
        }
        if (hasThread && !string.IsNullOrWhiteSpace(candidateThreadId))
        {
            return true;
        }
        return false;
    }

    private string FormatEvictedDeliveryReason() =>
        $"The result from {DisplayTitle} on {_hostName} could not be recovered because older conversation history was truncated before it was observed; the turn may already have finished there. Check the session on that host.";

    private string FormatStoppedReason(string? partialText)
    {
        if (string.IsNullOrWhiteSpace(partialText))
        {
            return $"{DisplayTitle} on {_hostName} was stopped before it finished.";
        }
        var trimmed = partialText.Trim();
        var snippet = trimmed.Length > PartialCancelSummaryChars ? trimmed[..PartialCancelSummaryChars] + "…" : trimmed;
        return $"{DisplayTitle} on {_hostName} was stopped before it finished. Partial output:\n{snippet}";
    }

    private string FormatAttachFailure(Exception e) =>
        $"{DisplayTitle} on {_hostName} could not be followed ({e.Message}); the agent may still be running there. Check the session on that host.";

    private void AbortAllPending(string reason, IReadOnlySet<string>? onlyDeliveryIds = null)
    {
        if (_coordinator is CollabCoordinator coordinator)
        {
            coordinator.NotifySessionAbortedAllPending(ItemId, reason, failed: true, onlyDeliveryIds);
        }
        else
        {
            _coordinator.NotifyTurnAborted(ItemId, reason, failed: true);
        }
    }

    private void TerminateWithFailure(string reason, IReadOnlySet<string>? onlyDeliveryIds = null)
    {
        List<string> clear;
        lock (_gate)
        {
            if (_disposed || _terminated) return;
            _terminated = true;
            _freshLaunchPending = false;
            _reattaching = false;
            _hasSnapshot = false;
            _trackedDeliveries.Clear();
            ResetEarlyBufferLocked();
            _state = AcpStates.Error;
            _errorText = reason;
            _errorReported = true;
            clear = [.. _bubbled];
            _bubbled.Clear();
        }
        try { _disposeCts.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var id in clear) _coordinator.ClearChildApproval(ItemId, id);
        AbortAllPending(reason, onlyDeliveryIds);
        _closed(this);
    }

    private void RaiseApproval(AcpEntryMessage entry)
    {
        var card = entry.Permission!;
        var allow = PickOption(card.Options, "allow_once", "allow");
        var reject = PickOption(card.Options, "reject_once", "reject");
        _coordinator.ReportChildApproval(new CollabBubbledApproval(
            entry.Id,
            _parentItemId!,
            ItemId,
            AgentId,
            $"{DisplayTitle} @ {_hostName}",
            card.Title,
            card.Detail,
            card.Warning,
            allowed =>
            {
                var option = allowed ? allow : reject;
                if (option is null) return;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _host.RespondAsync(new AcpRespondMessage { ItemId = ItemId, EntryId = entry.Id, OptionId = option.Id }, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is IOException or RemoteException or InvalidOperationException or ObjectDisposedException)
                    {
                    }
                });
            }));
    }

    private static AcpOptionMessage? PickOption(List<AcpOptionMessage> options, string exactKind, string kindPrefix) =>
        options.FirstOrDefault(o => o.Detail == exactKind)
        ?? options.FirstOrDefault(o => o.Detail?.StartsWith(kindPrefix, StringComparison.Ordinal) == true);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private void OnDisconnected() =>
        TerminateWithFailure($"Lost the connection to {_hostName}; the agent may still be running there. Check the session on that host.");

    public void Dispose()
    {
        bool shouldDetach;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            shouldDetach = _activated;
            _reattaching = false;
            _hasSnapshot = false;
            _trackedDeliveries.Clear();
            ResetEarlyBufferLocked();
        }
        try { _disposeCts.Cancel(); } catch (ObjectDisposedException) { }
        _host.AcpUpdated -= OnUpdate;
        if (shouldDetach && !_host.Completion.IsCompleted)
        {
            _ = Task.Run(async () =>
            {
                try { await _host.DetachConversationAsync(ItemId, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or RemoteException or InvalidOperationException or ObjectDisposedException) { }
            });
        }
    }
}
