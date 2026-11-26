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

    private readonly ICollabRemoteHost _host;
    private readonly ICollabCoordinator _coordinator;
    private readonly string _hostName;
    private readonly string? _parentItemId;
    private readonly Action<RemoteCollabSession> _closed;
    private readonly object _gate = new();
    private readonly List<AcpEntryMessage> _entries = [];
    private readonly HashSet<string> _bubbled = new(StringComparer.Ordinal);
    private long _sequence;
    private string _state = AcpStates.Connecting;
    private string? _statusDetail;
    private string? _errorText;
    private string? _lastReportedAssistantId;
    private bool _errorReported;
    private bool _disposed;
    private int _reattaching;

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
        Action<RemoteCollabSession> closed)
    {
        _host = host;
        _hostName = hostName;
        ItemId = itemId;
        AgentId = agentId;
        DisplayTitle = title;
        WorkingDirectory = workingDirectory;
        _parentItemId = parentItemId;
        Fresh = fresh;
        _coordinator = coordinator;
        _closed = closed;
    }

    public string ItemId { get; }
    public string AgentId { get; }
    public string DisplayTitle { get; }
    public string WorkingDirectory { get; }
    public string HostName => _hostName;
    private bool Fresh { get; }

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
            lock (_gate) return !_disposed && !_host.Completion.IsCompleted && _state != AcpStates.Error;
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
        _host.AcpUpdated += OnUpdate;
        _ = _host.Completion.ContinueWith(_ => OnDisconnected(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var snapshot = await _host.AttachConversationAsync(ItemId, ct).ConfigureAwait(false);
        ApplySnapshot(snapshot, markExistingAnswerSeen: !Fresh);
        Evaluate();
    }

    /* ---------- ICollabSessionBridge ---------- */

    public async Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default)
    {
        if (_disposed) return false;
        try
        {
            await _host.SendPromptAsync(ItemId, promptText, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is IOException or RemoteException or InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    // The host queues prompts itself and offers no way to withdraw one, so nothing can be taken back here.
    public int CancelQueuedCollabPrompts(string? threadId = null, string? taskId = null) => 0;

    public async Task CancelActiveTurnAsync()
    {
        if (_disposed) return;
        try { await _host.CancelTurnAsync(ItemId, CancellationToken.None).ConfigureAwait(false); }
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
        lock (_gate)
        {
            if (_disposed) return;
            if (update.Sequence != _sequence + 1)
            {
                // Missed something (or a duplicate): a fresh snapshot is the only safe state.
                if (update.Sequence > _sequence) ScheduleReattach();
                return;
            }
            _sequence = update.Sequence;
            foreach (var id in update.Removed)
            {
                _entries.RemoveAll(e => e.Id == id);
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
            if (update.State is { } state) _state = state;
            if (update.StatusDetail is not null) _statusDetail = update.StatusDetail;
            if (update.ErrorText is not null) _errorText = update.ErrorText;
        }
        Evaluate();
    }

    private void ApplySnapshot(AcpSnapshotMessage snapshot, bool markExistingAnswerSeen)
    {
        lock (_gate)
        {
            _sequence = snapshot.Sequence;
            _entries.Clear();
            _entries.AddRange(snapshot.Entries);
            _state = snapshot.State;
            _statusDetail = snapshot.StatusDetail;
            _errorText = snapshot.ErrorText;
            if (markExistingAnswerSeen)
            {
                _lastReportedAssistantId = _entries.LastOrDefault(e => e.Kind == AcpEntryKinds.Assistant)?.Id;
            }
        }
    }

    private void ScheduleReattach()
    {
        if (Interlocked.Exchange(ref _reattaching, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var snapshot = await _host.AttachConversationAsync(ItemId, CancellationToken.None).ConfigureAwait(false);
                ApplySnapshot(snapshot, markExistingAnswerSeen: false);
                Evaluate();
            }
            catch (Exception e) when (e is IOException or RemoteException or InvalidOperationException or ObjectDisposedException)
            {
            }
            finally
            {
                Volatile.Write(ref _reattaching, 0);
            }
        });
    }

    /// <summary>Turns the timeline's state into collaboration events: an answer, a failure, permission cards to raise or clear.</summary>
    private void Evaluate()
    {
        string? answer = null;
        string? failure = null;
        List<AcpEntryMessage> raise = [];
        List<string> clear = [];

        lock (_gate)
        {
            if (_disposed) return;

            if (_state == AcpStates.Error)
            {
                if (!_errorReported)
                {
                    _errorReported = true;
                    failure = $"{DisplayTitle} on {_hostName} failed: {_errorText ?? "the agent could not be started or its connection broke."}";
                }
            }
            else
            {
                _errorReported = false;
            }

            // A turn ended when the conversation is ready again and its last word is a finished answer after the last prompt.
            if (_state == AcpStates.Ready)
            {
                var lastUser = _entries.FindLastIndex(e => e.Kind == AcpEntryKinds.User);
                var lastAssistant = _entries.FindLastIndex(e => e.Kind == AcpEntryKinds.Assistant);
                if (lastUser >= 0 && lastAssistant > lastUser
                    && _entries[lastAssistant] is { IsStreaming: false } finished
                    && finished.Id != _lastReportedAssistantId)
                {
                    _lastReportedAssistantId = finished.Id;
                    if (!string.IsNullOrWhiteSpace(finished.Text)) answer = finished.Text;
                    else failure = finished.ErrorText is { Length: > 0 } err ? $"{DisplayTitle} on {_hostName} failed: {err}" : null;
                    answer ??= failure is null ? "(The remote session finished its turn with no text output.)" : null;
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
        }

        foreach (var id in clear) _coordinator.ClearChildApproval(ItemId, id);
        foreach (var entry in raise) RaiseApproval(entry);
        if (failure is not null && answer is null) _coordinator.NotifyTurnAborted(ItemId, failure, failed: true);
        if (answer is not null) _coordinator.NotifyTurnCompleted(ItemId, answer);
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

    private void OnDisconnected()
    {
        lock (_gate)
        {
            if (_disposed) return;
        }
        _coordinator.NotifyTurnAborted(
            ItemId,
            $"Lost the connection to {_hostName}; the agent may still be running there. Check the session on that host.",
            failed: true);
        _closed(this);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _host.AcpUpdated -= OnUpdate;
        if (!_host.Completion.IsCompleted)
        {
            _ = Task.Run(async () =>
            {
                try { await _host.DetachConversationAsync(ItemId, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or RemoteException or InvalidOperationException or ObjectDisposedException) { }
            });
        }
    }
}
