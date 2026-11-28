using System.Runtime.CompilerServices;
using Octopus.Agents.Signals;
using Octopus.App.Activity;
using Octopus.Terminal.Avalonia;

namespace Octopus.App.Collab.Sessions;

internal readonly record struct TerminalTurnAnswer(string Text, string? ThreadId, string? TaskId);

/// <summary>
/// A native terminal (a CLI agent such as Claude Code or Codex, or a shell) as a collaboration peer: prompts are pasted
/// into its PTY, its answer is the screen text that appeared after the prompt, Esc interrupts its turn. The PTY is
/// looked up on every call, so a restarted terminal keeps working.
/// </summary>
internal sealed class TerminalCollabSession(
    string itemId,
    string agentId,
    string title,
    string workingDirectory,
    TerminalSessionRegistry terminals,
    IItemActivityService? activity,
    Action? onInitialTurnConsumed = null,
    Action<string>? onLateRetiredDelivery = null,
    Action<string, string>? onDeliveryAccepted = null) : ICollabSessionBridge, IDisposable
{
    private sealed class DeliveryTicket(TerminalSession session, long generation, string deliveryId, bool expectReply)
    {
        public TerminalSession Session => session;
        public long Generation => generation;
        public string DeliveryId => deliveryId;
        public bool ExpectReply => expectReply;
        public bool Consumed;
    }

    private sealed class BoundedDeliverySet
    {
        private readonly LinkedList<string> _order = new();
        private readonly Dictionary<string, LinkedListNode<string>> _nodes = new(StringComparer.Ordinal);

        public int Count => _nodes.Count;

        public void Add(string deliveryId)
        {
            if (_nodes.ContainsKey(deliveryId)) return;
            while (_nodes.Count >= MaxTrackedAcceptedDeliveries && _order.First is { } oldest)
            {
                _order.RemoveFirst();
                _nodes.Remove(oldest.Value);
            }
            _nodes[deliveryId] = _order.AddLast(deliveryId);
        }

        public void Remove(string deliveryId)
        {
            if (_nodes.Remove(deliveryId, out var node))
            {
                _order.Remove(node);
            }
        }

        public bool Contains(string deliveryId) => _nodes.ContainsKey(deliveryId);

        public void CopyTo(HashSet<string> target)
        {
            foreach (var id in _nodes.Keys)
            {
                target.Add(id);
            }
        }
    }

    private readonly record struct PendingTerminalTurn(
        long Id,
        long TurnOrder,
        TerminalSession Session,
        long Generation,
        string? LinkId,
        long SubmittedAtMs,
        int Baseline,
        string? ThreadId,
        string? TaskId,
        string? DeliveryId,
        DeliveryTicket? Ticket,
        bool ExpectReply,
        bool IsInitialLaunch,
        bool IsManual);

    internal const int MaxPendingTurns = 64;
    internal const int MaxTrackedAcceptedDeliveries = 512;
    private const int MaxReadChars = 4000;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _pasteGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly List<PendingTerminalTurn> _turns = [];
    private readonly Queue<DeliveryTicket?> _evictedTurnTickets = new();
    private readonly Dictionary<TerminalSession, Dictionary<long, BoundedDeliverySet>> _acceptedDeliveriesBySession = new(ReferenceEqualityComparer.Instance);
    private readonly ConditionalWeakTable<TerminalSession, HashSet<long>> _retiredLinks = new();
    private TerminalSession? _activeSession;
    private long _activeGeneration;
    private bool _hadPriorLink;
    private long _evictedTurns;
    private long _nextTurnId;
    private int _activeDeliveries;
    private bool _disposed;
    private bool _primitivesDisposed;

    public string ItemId => itemId;
    public string AgentId => agentId;
    public string DisplayTitle => title;
    public string WorkingDirectory => workingDirectory;
    public bool IsBusy => activity?.Of(itemId) == ItemActivity.Working;
    public bool IsConnected => !_disposed && terminals.Find(itemId)?.IsRunning == true;
    public string? CurrentStatusDetail => null;
    public bool CanInferPendingOwnership => false;

    /// <summary>
    /// Seeds the initial CLI launch turn (whose prompt was supplied as a command-line argument rather than pasted)
    /// so the first Done signal correlates to that launch instead of being dropped or consuming a later queued job.
    /// </summary>
    internal bool ReserveInitialTurn(TerminalSession term, string? threadId, string? taskId, bool expectReply, string? deliveryId = null)
    {
        var textLength = term.ReadText().Length;
        string? acceptedLinkId = null;
        lock (_gate)
        {
            if (_disposed) return false;
            var generation = term.Generation;
            var linkId = term.CurrentLinkId;
            SyncSessionAndManualTurnsLocked(term, generation, textLength);
            if (_turns.Any(t => t.IsInitialLaunch)) return false;
            var ticket = expectReply && !string.IsNullOrWhiteSpace(deliveryId)
                ? new DeliveryTicket(term, generation, deliveryId, expectReply: true)
                : null;
            InsertTurnOrderedLocked(new PendingTerminalTurn(
                ++_nextTurnId,
                TurnOrder: 0,
                term,
                generation,
                linkId,
                SubmittedAtMs: term.CurrentLinkStartedAtMs,
                Baseline: 0,
                threadId,
                taskId,
                deliveryId,
                ticket,
                expectReply,
                IsInitialLaunch: true,
                IsManual: false));
            if (ticket is not null)
            {
                if (onDeliveryAccepted is not null && !string.IsNullOrWhiteSpace(linkId))
                {
                    acceptedLinkId = linkId;
                }
                else
                {
                    TrackAcceptedDeliveryLocked(term, generation, ticket.DeliveryId);
                }
            }
        }
        if (acceptedLinkId is not null && !string.IsNullOrWhiteSpace(deliveryId))
        {
            onDeliveryAccepted?.Invoke(deliveryId, acceptedLinkId);
        }
        return true;
    }

    public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId = null, string? taskId = null, CancellationToken cancellationToken = default) =>
        DeliverCollabPromptAsync(promptText, threadId, taskId, expectReply: true, deliveryId: null, shouldCommit: null, cancellationToken);

    public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId, string? taskId, bool expectReply, CancellationToken cancellationToken) =>
        DeliverCollabPromptAsync(promptText, threadId, taskId, expectReply, deliveryId: null, shouldCommit: null, cancellationToken);

    public Task<bool> DeliverCollabPromptAsync(string promptText, string? threadId, string? taskId, bool expectReply, string? deliveryId, CancellationToken cancellationToken) =>
        DeliverCollabPromptAsync(promptText, threadId, taskId, expectReply, deliveryId, shouldCommit: null, cancellationToken);

    public async Task<bool> DeliverCollabPromptAsync(
        string promptText,
        string? threadId,
        string? taskId,
        bool expectReply,
        string? deliveryId,
        Func<bool>? shouldCommit,
        CancellationToken cancellationToken)
    {
        if (shouldCommit is not null && !shouldCommit()) return false;

        CancellationToken disposeToken;
        lock (_gate)
        {
            if (_disposed) return false;
            _activeDeliveries++;
            disposeToken = _disposeCts.Token;
        }

        var gateAcquired = false;
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposeToken);
            var linkedToken = linkedCts.Token;

            await _pasteGate.WaitAsync(linkedToken).ConfigureAwait(false);
            gateAcquired = true;

            if (_disposed || (shouldCommit is not null && !shouldCommit())) return false;
            if (terminals.Find(itemId) is not { IsRunning: true } term) return false;
            var generation = term.Generation;
            var linkId = term.CurrentLinkId;
            if (!term.TryBeginCollabSubmission(generation, out var submissionScope))
            {
                return false;
            }

            using (submissionScope)
            {
                var baseline = term.ReadText().Length;

                // One clean submission: bracketed paste when the TUI supports it, otherwise newlines folded into spaces.
                var block = promptText.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
                string commitPayload;
                if (term.CachedInputModes.BracketedPaste)
                {
                    if (!term.TryEnqueueCollabWrite($"\x1b[200~{block}\x1b[201~", generation, linkedToken, assignTurnOrder: false, out _, out var pasteCompletion))
                    {
                        if (cancellationToken.IsCancellationRequested && !disposeToken.IsCancellationRequested)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        return false;
                    }
                    var pasteAccepted = await pasteCompletion.ConfigureAwait(false);
                    if (!pasteAccepted)
                    {
                        if (cancellationToken.IsCancellationRequested && !disposeToken.IsCancellationRequested)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        return false;
                    }
                    submissionScope!.MarkPasteLanded();
                    await Task.Delay(60, linkedToken).ConfigureAwait(false);
                    linkedToken.ThrowIfCancellationRequested();
                    commitPayload = "\r";
                }
                else
                {
                    commitPayload = block.Replace("\n", " ", StringComparison.Ordinal) + "\r";
                }

                if (shouldCommit is not null && !shouldCommit()) return false;

                PendingTerminalTurn reservation;
                DeliveryTicket? ticket = null;
                Task<bool> commitCompletion;
                bool initialConsumed;
                lock (_gate)
                {
                    if (_disposed) return false;
                    if (!ReferenceEquals(terminals.Find(itemId), term) || !term.IsRunning || term.Generation != generation)
                    {
                        return false;
                    }
                    initialConsumed = SyncSessionAndManualTurnsLocked(term, generation, baseline);
                    var submittedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (!term.TryEnqueueCollabWrite(commitPayload, generation, linkedToken, assignTurnOrder: true, out var turnOrder, out commitCompletion))
                    {
                        reservation = default;
                    }
                    else
                    {
                        if (expectReply && !string.IsNullOrWhiteSpace(deliveryId))
                        {
                            ticket = new DeliveryTicket(term, generation, deliveryId, expectReply: true);
                        }
                        reservation = new PendingTerminalTurn(
                            ++_nextTurnId,
                            turnOrder,
                            term,
                            generation,
                            linkId,
                            submittedAtMs,
                            baseline,
                            threadId,
                            taskId,
                            deliveryId,
                            ticket,
                            expectReply,
                            IsInitialLaunch: false,
                            IsManual: false);
                        if (InsertTurnOrderedLocked(reservation)) initialConsumed = true;
                    }
                }

                if (initialConsumed) onInitialTurnConsumed?.Invoke();
                if (reservation.Id == 0)
                {
                    if (cancellationToken.IsCancellationRequested && !disposeToken.IsCancellationRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    return false;
                }

                var accepted = false;
                try
                {
                    // Settle the irreversible commit write and its reservation together: caller cancellation can only
                    // abort before WriteLoopAsync begins writing, after which commitCompletion reports the actual write result.
                    accepted = await commitCompletion.ConfigureAwait(false);
                }
                finally
                {
                    if (!accepted)
                    {
                        lock (_gate)
                        {
                            RemoveTurnByIdLocked(reservation.Id);
                        }
                    }
                }

                if (accepted)
                {
                    string? lateRetiredDeliveryId = null;
                    string? acceptedOwnerDeliveryId = null;
                    if (ticket is not null)
                    {
                        lock (_gate)
                        {
                            if (!ticket.Consumed && !_disposed)
                            {
                                if (onDeliveryAccepted is not null && !string.IsNullOrWhiteSpace(linkId))
                                {
                                    acceptedOwnerDeliveryId = ticket.DeliveryId;
                                }
                                else if (IsLinkRetiredLocked(term, generation))
                                {
                                    lateRetiredDeliveryId = ticket.DeliveryId;
                                }
                                else
                                {
                                    TrackAcceptedDeliveryLocked(term, generation, ticket.DeliveryId);
                                }
                            }
                        }
                    }
                    submissionScope!.MarkCommitted();
                    if (acceptedOwnerDeliveryId is not null && !string.IsNullOrWhiteSpace(linkId))
                    {
                        onDeliveryAccepted?.Invoke(acceptedOwnerDeliveryId, linkId);
                    }
                    else if (lateRetiredDeliveryId is not null)
                    {
                        onLateRetiredDelivery?.Invoke(lateRetiredDeliveryId);
                    }
                    return !_disposed;
                }
                if (cancellationToken.IsCancellationRequested && !disposeToken.IsCancellationRequested)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return false;
            }
        }
        catch (OperationCanceledException) when (disposeToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (gateAcquired)
            {
                _pasteGate.Release();
            }
            ReleaseDeliveryAndMaybeDisposePrimitives();
        }
    }

    // The CLI keeps its own input queue; nothing sent can be taken back.
    public int CancelQueuedCollabPrompts(string? threadId = null, string? taskId = null) => 0;

    public Task CancelActiveTurnAsync() => CancelActiveTurnAsync(threadId: null, taskId: null);

    public Task CancelActiveTurnAsync(string? threadId, string? taskId)
    {
        var hasTarget = !string.IsNullOrWhiteSpace(threadId) || !string.IsNullOrWhiteSpace(taskId);
        var current = terminals.Find(itemId);
        if (current is not { IsRunning: true }) return Task.CompletedTask;
        var generation = current.Generation;
        var textLength = current.ReadText().Length;

        bool initialConsumed;
        bool shouldInterrupt;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            initialConsumed = SyncSessionAndManualTurnsLocked(current, generation, textLength);
            if (!hasTarget)
            {
                shouldInterrupt = true;
            }
            else if (_evictedTurns > 0 || _turns.Count == 0 || _turns[0].IsManual)
            {
                shouldInterrupt = false;
            }
            else
            {
                shouldInterrupt = MatchesTurn(_turns[0], threadId, taskId);
            }
        }

        if (initialConsumed) onInitialTurnConsumed?.Invoke();
        if (shouldInterrupt)
        {
            // Esc interrupts the running turn in Claude Code and Codex CLI without quitting the TUI.
            current.TrySendCollabControl("\x1b", generation);
        }
        return Task.CompletedTask;
    }

    public string ReadRecentSummary(int maxTurns = 5)
    {
        var text = terminals.Find(itemId)?.ReadText().Trim() ?? "";
        if (text.Length == 0) return "(Terminal buffer is currently empty.)";
        return text.Length > MaxReadChars ? "…\n" + text[^MaxReadChars..] : text;
    }

    /// <summary>
    /// What appeared on screen since the oldest outstanding prompt was pasted (its answer), cut to the last
    /// <paramref name="maxChars"/>; null when no prompt is outstanding or the finished prompt did not expect a reply.
    /// Taking it removes that turn from the queue and advances the next queued turn's baseline to the current screen length.
    /// </summary>
    public TerminalTurnAnswer? TakeAnswer(int maxChars) =>
        TakeAnswer(maxChars, signal: null, linkReplaced: false);

    internal TerminalTurnAnswer? TakeAnswer(int maxChars, AgentSignal? signal, bool linkReplaced = false)
    {
        var current = terminals.Find(itemId);
        var currentGeneration = current?.Generation;
        var text = current?.ReadText() ?? "";

        PendingTerminalTurn turn = default;
        var hasTurn = false;
        var initialConsumed = false;
        lock (_gate)
        {
            if (_disposed) return null;
            if (current is not null && currentGeneration is { } gen)
            {
                initialConsumed = SyncSessionAndManualTurnsLocked(current, gen, text.Length);
            }
            if (signal is not null)
            {
                if (current is null || !IsSignalValidForLink(current, signal, linkReplaced || _hadPriorLink))
                {
                    if (initialConsumed) onInitialTurnConsumed?.Invoke();
                    return null;
                }
            }
            if (_evictedTurns > 0)
            {
                _evictedTurns--;
                if (_evictedTurnTickets.Count > 0 && _evictedTurns < _evictedTurnTickets.Count)
                {
                    var evictedTicket = _evictedTurnTickets.Dequeue();
                    if (evictedTicket is not null)
                    {
                        evictedTicket.Consumed = true;
                        RemoveAcceptedDeliveryLocked(evictedTicket.Session, evictedTicket.Generation, evictedTicket.DeliveryId);
                    }
                }
                if (_turns.Count > 0)
                {
                    var next = _turns[0];
                    _turns[0] = next with { Baseline = Math.Max(next.Baseline, text.Length) };
                }
            }
            else if (_turns.Count > 0)
            {
                var head = _turns[0];
                if (signal is not null
                    && string.IsNullOrWhiteSpace(signal.LinkId)
                    && signal.SentAt > 0
                    && !head.IsManual
                    && head.SubmittedAtMs > 0
                    && signal.SentAt < head.SubmittedAtMs)
                {
                    if (initialConsumed) onInitialTurnConsumed?.Invoke();
                    return null;
                }
                turn = head;
                _turns.RemoveAt(0);
                if (turn.Ticket is { } consumedTicket)
                {
                    consumedTicket.Consumed = true;
                    RemoveAcceptedDeliveryLocked(consumedTicket.Session, consumedTicket.Generation, consumedTicket.DeliveryId);
                }
                else
                {
                    RemoveAcceptedDeliveryLocked(turn.Session, turn.Generation, turn.DeliveryId);
                }
                hasTurn = true;
                if (turn.IsInitialLaunch) initialConsumed = true;
                if (_turns.Count > 0)
                {
                    var next = _turns[0];
                    _turns[0] = next with { Baseline = Math.Max(next.Baseline, text.Length) };
                }
            }
            else if (current is not null && currentGeneration is { } activeGen)
            {
                // No earlier accepted turns exist; let an in-flight provisional (or just-committed) manual turn
                // claim this early Done directly without ever importing provisional orders into _turns.
                current.TryConsumeLeadingManualTurn(activeGen);
            }
        }

        if (initialConsumed) onInitialTurnConsumed?.Invoke();
        if (!hasTurn || turn.IsManual || !turn.ExpectReply) return null;
        var delta = (turn.Baseline < text.Length ? text[turn.Baseline..] : text).Trim();
        if (delta.Length > maxChars) delta = "…\n" + delta[^maxChars..];
        var answer = delta.Length == 0 ? "Terminal session reported turn completion." : delta;
        return new TerminalTurnAnswer(answer, turn.ThreadId, turn.TaskId);
    }

    internal static bool IsSignalValidForLink(TerminalSession current, AgentSignal signal, bool linkReplaced)
    {
        var activeLinkId = current.CurrentLinkId;
        if (!string.IsNullOrWhiteSpace(signal.LinkId))
        {
            return !string.IsNullOrWhiteSpace(activeLinkId)
                && string.Equals(signal.LinkId, activeLinkId, StringComparison.Ordinal);
        }

        // Unidentified signal: once a link has been restarted or replaced on this item, an unidentified signal
        // cannot be attributed to the fresh link rather than the replaced one.
        if (linkReplaced || current.Generation > 1)
        {
            return false;
        }

        if (signal.SentAt > 0 && current.CurrentLinkStartedAtMs > 0 && signal.SentAt < current.CurrentLinkStartedAtMs)
        {
            return false;
        }

        return true;
    }

    internal IReadOnlySet<string>? TakeAcceptedDeliveries(TerminalSession session) =>
        TakeAcceptedDeliveries(session, generation: null);

    internal IReadOnlySet<string>? TakeAcceptedDeliveries(TerminalSession session, long? generation)
    {
        var isLive = ReferenceEquals(terminals.Find(itemId), session)
            && session.IsRunning
            && (generation is null || session.Generation == generation.Value);
        lock (_gate)
        {
            if (!isLive)
            {
                MarkLinkRetiredLocked(session, generation);
            }
            if (!_acceptedDeliveriesBySession.TryGetValue(session, out var byGeneration))
            {
                return null;
            }
            if (generation is null)
            {
                _acceptedDeliveriesBySession.Remove(session);
                HashSet<string>? all = null;
                foreach (var set in byGeneration.Values)
                {
                    if (set.Count == 0) continue;
                    all ??= new HashSet<string>(StringComparer.Ordinal);
                    set.CopyTo(all);
                }
                return all is { Count: > 0 } ? all : null;
            }
            if (!byGeneration.Remove(generation.Value, out var genSet))
            {
                return null;
            }
            if (byGeneration.Count == 0)
            {
                _acceptedDeliveriesBySession.Remove(session);
            }
            if (genSet.Count == 0)
            {
                return null;
            }
            var result = new HashSet<string>(StringComparer.Ordinal);
            genSet.CopyTo(result);
            return result;
        }
    }

    internal bool HasAcceptedDeliveryForOtherSession(TerminalSession retiredSession, string? deliveryId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId)) return false;
        lock (_gate)
        {
            foreach (var (session, byGeneration) in _acceptedDeliveriesBySession)
            {
                if (ReferenceEquals(session, retiredSession)) continue;
                foreach (var set in byGeneration.Values)
                {
                    if (set.Contains(deliveryId))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }

    public void Dispose()
    {
        bool shouldCancel;
        bool disposePrimitivesNow;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _turns.Clear();
            _evictedTurnTickets.Clear();
            _acceptedDeliveriesBySession.Clear();
            _evictedTurns = 0;
            shouldCancel = !_primitivesDisposed;
            disposePrimitivesNow = _activeDeliveries == 0 && !_primitivesDisposed;
            if (disposePrimitivesNow) _primitivesDisposed = true;
        }
        if (shouldCancel)
        {
            try { _disposeCts.Cancel(); } catch (ObjectDisposedException) { }
        }
        if (disposePrimitivesNow)
        {
            _pasteGate.Dispose();
            _disposeCts.Dispose();
        }
    }

    private void ReleaseDeliveryAndMaybeDisposePrimitives()
    {
        bool disposePrimitivesNow;
        lock (_gate)
        {
            _activeDeliveries--;
            disposePrimitivesNow = _disposed && _activeDeliveries == 0 && !_primitivesDisposed;
            if (disposePrimitivesNow) _primitivesDisposed = true;
        }
        if (disposePrimitivesNow)
        {
            _pasteGate.Dispose();
            _disposeCts.Dispose();
        }
    }

    private bool SyncSessionAndManualTurnsLocked(TerminalSession current, long currentGeneration, int currentTextLength)
    {
        if (_activeSession is not null && (!ReferenceEquals(_activeSession, current) || _activeGeneration != currentGeneration))
        {
            _hadPriorLink = true;
            _evictedTurns = 0;
            _evictedTurnTickets.Clear();
        }
        var prunedInitial = PruneStaleTurnsLocked(current, currentGeneration);
        _activeSession = current;
        _activeGeneration = currentGeneration;

        List<long>? manualOrders = null;
        current.DrainManualTurns(currentGeneration, ref manualOrders, out var manualEvicted);
        if (manualEvicted > 0)
        {
            _evictedTurns += manualEvicted;
        }
        if (manualOrders is { Count: > 0 })
        {
            var linkId = current.CurrentLinkId;
            foreach (var order in manualOrders)
            {
                var manualTurn = new PendingTerminalTurn(
                    Id: 0,
                    TurnOrder: order,
                    Session: current,
                    Generation: currentGeneration,
                    LinkId: linkId,
                    SubmittedAtMs: 0,
                    Baseline: currentTextLength,
                    ThreadId: null,
                    TaskId: null,
                    DeliveryId: null,
                    Ticket: null,
                    ExpectReply: false,
                    IsInitialLaunch: false,
                    IsManual: true);
                if (InsertTurnOrderedLocked(manualTurn)) prunedInitial = true;
            }
        }
        return prunedInitial;
    }

    private void MarkLinkRetiredLocked(TerminalSession session, long? generation)
    {
        var retired = _retiredLinks.GetOrCreateValue(session);
        retired.Add(generation ?? 0L);
    }

    private bool IsLinkRetiredLocked(TerminalSession session, long generation) =>
        _retiredLinks.TryGetValue(session, out var retired)
        && (retired.Contains(0L) || retired.Contains(generation));

    private void TrackAcceptedDeliveryLocked(TerminalSession session, long generation, string? deliveryId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId)) return;
        if (!_acceptedDeliveriesBySession.TryGetValue(session, out var byGeneration))
        {
            byGeneration = new Dictionary<long, BoundedDeliverySet>();
            _acceptedDeliveriesBySession[session] = byGeneration;
        }
        if (!byGeneration.TryGetValue(generation, out var set))
        {
            set = new BoundedDeliverySet();
            byGeneration[generation] = set;
        }
        set.Add(deliveryId);
    }

    private void RemoveAcceptedDeliveryLocked(TerminalSession session, long generation, string? deliveryId)
    {
        if (string.IsNullOrWhiteSpace(deliveryId)) return;
        if (_acceptedDeliveriesBySession.TryGetValue(session, out var byGeneration)
            && byGeneration.TryGetValue(generation, out var set))
        {
            set.Remove(deliveryId);
            if (set.Count == 0)
            {
                byGeneration.Remove(generation);
                if (byGeneration.Count == 0)
                {
                    _acceptedDeliveriesBySession.Remove(session);
                }
            }
        }
    }

    private bool InsertTurnOrderedLocked(PendingTerminalTurn turn)
    {
        var evictedInitial = false;
        var index = _turns.Count;
        while (index > 0 && _turns[index - 1].TurnOrder > turn.TurnOrder)
        {
            index--;
        }
        _turns.Insert(index, turn);
        while (_turns.Count > MaxPendingTurns)
        {
            var evicted = _turns[0];
            if (evicted.IsInitialLaunch) evictedInitial = true;
            _turns.RemoveAt(0);
            _evictedTurns++;
            if (_evictedTurnTickets.Count >= MaxTrackedAcceptedDeliveries)
            {
                _evictedTurnTickets.Dequeue();
            }
            _evictedTurnTickets.Enqueue(evicted.Ticket is { ExpectReply: true } ticket ? ticket : null);
        }
        return evictedInitial;
    }

    private bool PruneStaleTurnsLocked(TerminalSession current, long currentGeneration)
    {
        var prunedInitial = false;
        for (var i = _turns.Count - 1; i >= 0; i--)
        {
            var entry = _turns[i];
            if (!ReferenceEquals(entry.Session, current) || entry.Generation != currentGeneration)
            {
                if (entry.IsInitialLaunch) prunedInitial = true;
                _turns.RemoveAt(i);
            }
        }
        return prunedInitial;
    }

    private void RemoveTurnByIdLocked(long id)
    {
        for (var i = _turns.Count - 1; i >= 0; i--)
        {
            if (_turns[i].Id == id)
            {
                _turns.RemoveAt(i);
                return;
            }
        }
    }

    private static bool MatchesTurn(PendingTerminalTurn turn, string? threadId, string? taskId)
    {
        if (turn.IsManual) return false;
        var hasThread = !string.IsNullOrWhiteSpace(threadId);
        var hasTask = !string.IsNullOrWhiteSpace(taskId);
        if (hasThread && hasTask && turn.ThreadId is not null && turn.TaskId is not null)
        {
            return string.Equals(turn.ThreadId, threadId, StringComparison.Ordinal)
                && string.Equals(turn.TaskId, taskId, StringComparison.Ordinal);
        }
        if (hasTask && turn.TaskId is not null)
        {
            return string.Equals(turn.TaskId, taskId, StringComparison.Ordinal);
        }
        if (hasThread && turn.ThreadId is not null)
        {
            return string.Equals(turn.ThreadId, threadId, StringComparison.Ordinal);
        }
        return false;
    }
}
