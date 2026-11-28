using System.Threading.Channels;
using Avalonia.Threading;
using Octopus.Core.Terminals;
using TKCode.Terminal.Core;

namespace Octopus.Terminal.Avalonia;

internal enum ManualInputKind
{
    Raw,
    NonEditing,
    Printable,
    Backspace,
    Submit,
    CancelLine,
    KillToStart,
    CursorOrEdit,
    UnobservableEdit,
}

/// <summary>
/// Tracks the outcome of an exclusive collaboration submission on a single terminal generation so disposal knows
/// whether a bracketed paste may have landed in the child editor without a committed Enter.
/// </summary>
public interface ICollabSubmissionScope : IDisposable
{
    /// <summary>Records that a bracketed paste payload was accepted by the PTY before the final commit Enter.</summary>
    void MarkPasteLanded();

    /// <summary>Records that the final commit write was accepted by the PTY.</summary>
    void MarkCommitted();
}

/// <summary>
/// Host-side recovery state on a live terminal link after a bracketed collaboration paste was cancelled before
/// commit. While present, automated collaboration submissions are fenced and any manual input buffered during the
/// cancelled submission is held in original order until the user takes an explicit recovery action or restarts.
/// </summary>
public sealed record TerminalPendingInputRecovery(
    bool HasUnresolvedCancelledPaste,
    bool HasHeldInput,
    string HeldInputPreview,
    string HeldInputText,
    byte[] HeldInputBytes)
{
    /// <summary>Monotonic identity of this recovery state, used to bind UI gestures to the displayed recovery.</summary>
    public long RecoveryId { get; init; }

    /// <summary>
    /// Six-argument constructor preserving binary and source compatibility (including PascalCase named arguments)
    /// with clients compiled against the six-field record shape.
    /// </summary>
#pragma warning disable IDE1006 // Naming Styles: match positional record parameter names from six-field shape
    public TerminalPendingInputRecovery(
        bool HasUnresolvedCancelledPaste,
        bool HasHeldInput,
        string HeldInputPreview,
        string HeldInputText,
        byte[] HeldInputBytes,
        long RecoveryId)
        : this(HasUnresolvedCancelledPaste, HasHeldInput, HeldInputPreview, HeldInputText, HeldInputBytes)
    {
        this.RecoveryId = RecoveryId;
    }

    /// <summary>
    /// Six-output deconstructor preserving binary and source compatibility (including PascalCase named out
    /// parameters) with clients deconstructing <see cref="RecoveryId"/> alongside the five primary fields.
    /// </summary>
    public void Deconstruct(
        out bool HasUnresolvedCancelledPaste,
        out bool HasHeldInput,
        out string HeldInputPreview,
        out string HeldInputText,
        out byte[] HeldInputBytes,
        out long RecoveryId)
    {
        HasUnresolvedCancelledPaste = this.HasUnresolvedCancelledPaste;
        HasHeldInput = this.HasHeldInput;
        HeldInputPreview = this.HeldInputPreview;
        HeldInputText = this.HeldInputText;
        HeldInputBytes = this.HeldInputBytes;
        RecoveryId = this.RecoveryId;
    }
#pragma warning restore IDE1006
}

/// <summary>
/// One terminal: a <see cref="TerminalCore"/> fed by a connection's output. One lock serialises every access to the
/// core. Events are raised on the UI thread. Effect callbacks run inside <c>Write</c>/<c>Resize</c> under that lock,
/// so they only queue work and never touch the core again.
/// </summary>
public sealed class TerminalSession : ITerminalEffects, IAsyncDisposable
{
    private sealed class HeldEntry(byte[] data, ManualInputKind kind, int charCount)
    {
        public byte[] Data { get; } = data;
        public ManualInputKind Kind { get; } = kind;
        public int CharCount { get; } = charCount;
    }

    private sealed class HeldReplayState(int totalEntries)
    {
        public int Remaining = totalEntries;
        public int AcceptedCount;
        public bool ResolvedLine;
        public bool AnyFailed;
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class WriteItem
    {
        private const int StatePending = 0;
        private const int StateStarted = 1;
        private const int StateCancelled = 2;

        private int _state;
        private readonly CancellationTokenRegistration _cancelReg;

        public byte[] Bytes { get; }
        public TaskCompletionSource<bool>? Ack { get; }
        public HeldEntry? HeldEntry { get; }
        public HeldReplayState? HeldReplay { get; }
        public bool ApplyManualOnAccept { get; }
        public ManualInputKind ManualKind { get; }
        public int ManualCharCount { get; }

        public WriteItem(
            byte[] bytes,
            TaskCompletionSource<bool>? ack,
            CancellationToken callerCancellation,
            HeldEntry? heldEntry = null,
            HeldReplayState? heldReplay = null,
            bool applyManualOnAccept = false,
            ManualInputKind manualKind = ManualInputKind.Raw,
            int manualCharCount = 0)
        {
            Bytes = bytes;
            Ack = ack;
            HeldEntry = heldEntry;
            HeldReplay = heldReplay;
            ApplyManualOnAccept = applyManualOnAccept;
            ManualKind = manualKind;
            ManualCharCount = manualCharCount;
            if (ack is not null && callerCancellation.CanBeCanceled)
            {
                _cancelReg = callerCancellation.Register(static state => ((WriteItem)state!).TryCancelBeforeStart(), this);
            }
        }

        public bool TryBeginWrite()
        {
            var won = Interlocked.CompareExchange(ref _state, StateStarted, StatePending) == StatePending;
            _cancelReg.Dispose();
            return won;
        }

        public void TryCancelBeforeStart()
        {
            if (Interlocked.CompareExchange(ref _state, StateCancelled, StatePending) == StatePending)
            {
                Ack?.TrySetResult(false);
            }
        }

        public void CancelDropped()
        {
            _cancelReg.Dispose();
            Ack?.TrySetResult(false);
        }
    }

    private sealed class Link
    {
        public const int MaxTrackedManualTurns = 64;

        private TerminalLaunch? _launch;

        public required long Generation { get; init; }
        public required string LinkId { get; init; }
        public required long StartedAtMs { get; init; }
        public required ITerminalConnection Connection { get; init; }
        public TerminalLaunch? Launch { init => _launch = value; }
        public Channel<WriteItem> Writes { get; } = Channel.CreateUnbounded<WriteItem>(new UnboundedChannelOptions { SingleReader = true });
        public CancellationTokenSource Cancel { get; } = new();
        public object WriteGate { get; } = new();
        public bool CollabSubmissionHeld;
        public bool HasUnresolvedCancelledPaste;
        public bool HeldReplayInFlight;
        public bool HasUncertainHeldWrite;
        public int PendingLiveManualAccounting;
        public long RecoveryId;
        public List<(byte[] Data, ManualInputKind Kind, int CharCount)> DeferredManualWrites { get; } = [];
        public List<HeldEntry> HeldManualWrites { get; } = [];
        /// <summary>0 = empty, &gt;0 = exact count of simple typed characters, -1 = indeterminate non-empty draft.</summary>
        public int ManualDraftChars;
        public bool HasManualDraft => ManualDraftChars != 0;
        public bool InManualBracketedPaste;
        public long ManualDraftEpoch;
        public long NextTurnOrder;
        public Queue<long> PendingManualTurns { get; } = new();
        public long EvictedManualTurns;
        // Provisional turn range for the single write currently in flight on WriteLoopAsync. Kept separate from
        // PendingManualTurns until WriteAsync succeeds so rejected writes never evict real turns or leak into bridges.
        public long InFlightFirstTurnOrder;
        public int InFlightTurnCount;
        public int InFlightConsumedCount;
        public volatile bool StopRequested;

        public void ReleaseLaunch() => Interlocked.Exchange(ref _launch, null)?.Dispose();
    }

    private struct ManualAccountingSnapshot
    {
        public int PrevDraftChars;
        public bool PrevInPaste;
        public long DraftEpoch;
        public bool ResolvedLine;
    }

    private sealed class CollabSubmissionScope(TerminalSession session, Link link) : ICollabSubmissionScope
    {
        private TerminalSession? _session = session;
        private Link? _link = link;
        private int _pasteLanded;
        private int _committed;

        public void MarkPasteLanded() => Volatile.Write(ref _pasteLanded, 1);

        public void MarkCommitted() => Volatile.Write(ref _committed, 1);

        public void Dispose()
        {
            var session = Interlocked.Exchange(ref _session, null);
            var link = Interlocked.Exchange(ref _link, null);
            if (session is not null && link is not null)
            {
                session.EndCollabSubmission(link, Volatile.Read(ref _pasteLanded) != 0, Volatile.Read(ref _committed) != 0);
            }
        }
    }

    private static long s_nextRecoveryId;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Func<string, TerminalLaunch?> _launchFactory;
    private LaunchSpec _spec;
    private readonly ITerminalConnectionFactory _factory;
    private long _lifecycleEpoch;
    private Task? _disposeTask;
    private Func<bool> _isVisible;
    private readonly List<ITerminalTap> _taps = [];
    private TerminalHistoryRing? _history;
    // The place in the output a tap is told: every byte takes one position, a resize or a reset takes one too.
    private ulong _head;
    /// <summary>
    /// What one terminal's scrollback may take. A full-screen program (an agent's TUI redraws the screen all the time)
    /// fills it long before the line limit, and the pages are never given back: measured on a running app, seven
    /// terminals at the old 16 MB held 111 MB, nearly all of it untouched and swapped out. 8 MB still keeps a few
    /// thousand wide, fully styled lines (4 MB kept under two thousand, too little to read back).
    /// </summary>
    public const long MaxScrollbackBytes = 8L * 1024 * 1024;
    private static readonly TimeSpan MinFrameInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan MaxRenderHold = TimeSpan.FromMilliseconds(150);

    private readonly int _scrollbackLines;
    private ColorScheme _initialScheme;
    private TerminalPalette _initialPalette;
    // A mirror's last request to its connection; its own grid follows the terminal it mirrors.
    private TerminalSize _requested;
    private object? _sizeOwner;
    // The views on other devices that claimed the size, the latest last: when the one that has the say leaves, it goes
    // back to the one before it, and to this app's own view when none is left.
    private readonly List<object> _claims = [];
    private TerminalCore? _core;
    private TerminalSize _size;
    private volatile Link? _link;
    private volatile bool _isRunning;
    private long _generation;
    private InputModes _cachedInputModes = InputModes.Defaults;
    private volatile bool _visible;
    private int _flushPending;
    private long _lastFlushTick;
    private Timer? _coalesceTimer;
    private Timer? _renderHoldTimer;
    private volatile bool _renderHeld;
    private volatile bool _dirtyDuringHold;
    private volatile bool _dirtyWhileHidden;
    private bool _disposed;

    /// <param name="spec">What to run.</param>
    /// <param name="factory">Starts the pty.</param>
    /// <param name="size">Initial grid.</param>
    /// <param name="palette">Initial default colours.</param>
    /// <param name="scheme">Light or dark.</param>
    /// <param name="scrollbackLines">Scrollback limit in lines (also capped at <see cref="MaxScrollbackBytes"/>).</param>
    /// <param name="isVisible">Whether the view is on screen; frames are only taken then.</param>
    public TerminalSession(LaunchSpec spec, ITerminalConnectionFactory factory, TerminalSize size,
        TerminalPalette palette, ColorScheme scheme, int scrollbackLines, Func<bool> isVisible)
        : this(_ => new TerminalLaunch(spec), spec, factory, size, palette, scheme, scrollbackLines, isVisible)
    {
    }

    public TerminalSession(Func<string, TerminalLaunch?> launchFactory, ITerminalConnectionFactory factory, TerminalSize size,
        TerminalPalette palette, ColorScheme scheme, int scrollbackLines, Func<bool> isVisible)
        : this(launchFactory, new LaunchSpec("", [], null, new Dictionary<string, string>()), factory, size, palette, scheme, scrollbackLines, isVisible)
    {
    }

    private TerminalSession(Func<string, TerminalLaunch?> launchFactory, LaunchSpec initialSpec, ITerminalConnectionFactory factory, TerminalSize size,
        TerminalPalette palette, ColorScheme scheme, int scrollbackLines, Func<bool> isVisible)
    {
        _launchFactory = launchFactory;
        _spec = initialSpec;
        _factory = factory;
        _size = size;
        _initialPalette = palette;
        _initialScheme = scheme;
        _scrollbackLines = scrollbackLines;
        _isVisible = isVisible;
        _visible = isVisible();
    }

    /// <summary>A new frame is ready to show.</summary>
    public event Action<FrameSnapshot>? FrameReady;

    /// <summary>The program set a new window title.</summary>
    public event Action<string>? TitleChanged;

    /// <summary>The program rang the bell.</summary>
    public event Action? Bell;

    /// <summary>The process ended and its output has been read to the end.</summary>
    public event Action<TerminalExit>? Exited;

    /// <summary>The process for a specific link generation ended and its output has been read to the end.</summary>
    public event Action<long, string, TerminalExit>? LinkExited;

    /// <summary>A live link was explicitly stopped (for example by restart or disposal) before exiting on its own.</summary>
    public event Action<long, string>? LinkStopped;

    /// <summary>Host-side pending input recovery state changed (entered, updated, or resolved). Raised on the thread that caused it.</summary>
    public event Action? PendingInputRecoveryChanged;

    /// <summary>True from <see cref="Start"/> until the process exits.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set => _isRunning = value;
    }

    /// <summary>Monotonically increasing process start generation (incremented on initial start and each restart).</summary>
    public long Generation => Volatile.Read(ref _generation);

    /// <summary>Unique identifier of the current running link, or null when no link is active.</summary>
    public string? CurrentLinkId => _link?.LinkId;

    /// <summary>Unique identifier of the most recently started link on this session, retained across stop and disposal.</summary>
    public string? LastLinkId => _lastLinkId;
    private volatile string? _lastLinkId;
    private readonly List<string> _startedLinkIds = [];

    /// <summary>Returns the identifiers of all process links started on this session, retained across restart, stop, and disposal.</summary>
    public IReadOnlyList<string> SnapshotLinkIds()
    {
        lock (_gate)
        {
            return _startedLinkIds.Count == 0 ? [] : [.. _startedLinkIds];
        }
    }

    /// <summary>UTC Unix millisecond timestamp when the current link started, or 0 when no link is active.</summary>
    public long CurrentLinkStartedAtMs => _link?.StartedAtMs ?? 0;

    /// <summary>What runs.</summary>
    public LaunchSpec Spec => _spec;

    /// <summary>The window title the program last set; null before it set one. Readable from any thread.</summary>
    public string? Title => _title;
    private volatile string? _title;

    /// <summary>How the last process ended; null while one runs or before the first. Readable from any thread.</summary>
    public TerminalExit? LastExit
    {
        get { lock (_gate) return _lastExit; }
        private set { lock (_gate) _lastExit = value; }
    }
    private TerminalExit? _lastExit;

    /// <summary>
    /// A mirror shows a terminal whose process another terminal owns (a session on another computer): it parses the
    /// same output, so it must not answer the program's queries a second time. Its replies are dropped; what the
    /// person types is still sent.
    /// </summary>
    public bool IsMirror { get; init; }

    /// <summary>
    /// The view that shows this session, or none: frames are only taken while <paramref name="isVisible"/> is true.
    /// A session lives without a view (started for another device, or its tab is not built yet) and can get one later.
    /// </summary>
    public void SetViewer(Func<bool>? isVisible)
    {
        _isVisible = isVisible ?? (static () => false);
        _visible = _isVisible();
        if (isVisible is not null && _visible) ForceFlush();
    }

    /// <summary>Updates the cached visibility flag from the UI thread so hidden sessions avoid posting render callbacks.</summary>
    public void SetVisible(bool visible)
    {
        var wasVisible = _visible;
        _visible = visible;
        if (visible && !wasVisible && _dirtyWhileHidden) RequestFlush();
    }

    /// <summary>
    /// Adds a second reader of the output, for a view somewhere else. In one step, with no output in between, it takes
    /// the terminal's state (<see cref="TerminalTapStart.Snapshot"/>, in <paramref name="format"/>) and starts
    /// reporting to <paramref name="tap"/> everything from <see cref="TerminalTapStart.Position"/> on: the snapshot
    /// followed by what the tap is told is exactly what this terminal showed, nothing twice and nothing missing.
    /// Disposing the result ends the reports.
    /// </summary>
    public IDisposable Tap(ITerminalTap tap, TerminalSnapshotFormat format, out TerminalTapStart start) =>
        Tap(tap, format, resumeFrom: null, out start);

    /// <summary>
    /// Adds a second reader of the output, resuming from <paramref name="resumeFrom"/> without encoding a snapshot when
    /// all output since that position is still in the session's 1 MiB history ring buffer. When <paramref name="resumeFrom"/>
    /// is null or no longer in the ring buffer, takes a full snapshot and sets <see cref="TerminalTapStart.Resumed"/> to false.
    /// </summary>
    public IDisposable Tap(ITerminalTap tap, TerminalSnapshotFormat format, ulong? resumeFrom, out TerminalTapStart start)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _core is null, this);
            if (_history is null)
            {
                _history = new TerminalHistoryRing();
                _history.ResetTo(_head);
            }
            if (resumeFrom is { } from && _history.TryReadFrom(from, _head, out var missed))
            {
                foreach (var item in missed)
                {
                    switch (item.Kind)
                    {
                        case TerminalHistoryKind.Output:
                            tap.OnOutput(item.Position, item.Bytes);
                            break;
                        case TerminalHistoryKind.Resize:
                            tap.OnResize(item.Position, item.Size);
                            break;
                        case TerminalHistoryKind.Reset:
                            tap.OnReset(item.Position);
                            break;
                    }
                }
                start = new TerminalTapStart(from, format, [], _size, Title, Resumed: true);
                _taps.Add(tap);
                return new TapRegistration(this, tap);
            }

            var snapshot = format == TerminalSnapshotFormat.Native
                ? _core.EncodeSnapshot()
                : System.Text.Encoding.UTF8.GetBytes(_core.FormatVt());
            start = new TerminalTapStart(_head, format, snapshot, _size, Title, Resumed: false);
            _taps.Add(tap);
        }
        return new TapRegistration(this, tap);
    }

    /// <summary>True when the foreground process is not the program that was started (a shell running something).</summary>
    public async Task<bool> HasRunningChildAsync()
    {
        if (!IsRunning) return false;
        // What runs on the other computer cannot be looked at from here: as long as the terminal is live, assume it matters.
        if (IsMirror) return true;
        var name = await GetForegroundProcessNameAsync().ConfigureAwait(false);
        return !string.IsNullOrEmpty(name) && !SameProgram(name, _spec.FileName);
    }

    private static bool SameProgram(string running, string started)
    {
        static string Normalize(string s) => Path.GetFileNameWithoutExtension(s.Trim()).TrimStart('-').ToLowerInvariant();
        return Normalize(running) == Normalize(started);
    }

    private sealed class TapRegistration(TerminalSession session, ITerminalTap tap) : IDisposable
    {
        public void Dispose()
        {
            lock (session._gate) session._taps.Remove(tap);
        }
    }

    /// <summary>Creates the core and starts the process. Throws when the process cannot start.</summary>
    public void Start()
    {
        if (!TryStart())
        {
            throw new InvalidOperationException("Terminal launch could not be planned.");
        }
    }

    /// <summary>Creates the core and starts the process; returns false without keeping a core when the launch factory returns null.</summary>
    internal bool TryStart()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _core = TerminalCore.Create(Options(_size), this);
            _core.SetDefaultColors(_initialPalette, _initialScheme);
            _cachedInputModes = _core.ReadInputModes();
            _requested = _size;
        }
        try
        {
            if (StartLink(allowNullLaunch: true, expectedEpoch: null, out var abortedConnection))
            {
                return true;
            }
            if (abortedConnection is not null)
            {
                _ = abortedConnection.DisposeAsync().AsTask();
            }
            lock (_gate) { _core?.Dispose(); _core = null; }
            return false;
        }
        catch
        {
            lock (_gate) { _core?.Dispose(); _core = null; }
            throw;
        }
    }

    /// <summary>Ends the current process if any, clears the screen and starts a freshly planned process.</summary>
    public async Task RestartAsync()
    {
        var opEpoch = Interlocked.Increment(ref _lifecycleEpoch);
        await _lifecycleGate.WaitAsync().ConfigureAwait(true);
        try
        {
            await StopLinkAsync().ConfigureAwait(true);
            lock (_gate)
            {
                if (_disposed || _core is null || Volatile.Read(ref _lifecycleEpoch) != opEpoch) return;
                _core.Reset();
                _core.SetDefaultColors(_initialPalette, _initialScheme);
                _cachedInputModes = _core.ReadInputModes();
                _history?.AppendReset(_head);
                foreach (var tap in _taps) tap.OnReset(_head);
                _head++;
            }
            LastExit = null;
            var started = StartLink(allowNullLaunch: false, expectedEpoch: opEpoch, out var abortedConnection);
            if (abortedConnection is not null)
            {
                try { await abortedConnection.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            if (!started)
            {
                return;
            }
            ForceFlush();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Runs <paramref name="action"/> on the core under the lock. Must not be called from effect callbacks.</summary>
    public T Read<T>(Func<TerminalCore, T> action, T fallback = default!)
    {
        lock (_gate) return _core is null ? fallback : action(_core);
    }

    /// <summary>Runs <paramref name="action"/> on the core under the lock, then refreshes the screen.</summary>
    public void Use(Action<TerminalCore> action)
    {
        lock (_gate)
        {
            if (_core is null) return;
            action(_core);
        }
        RequestFlush();
    }

    /// <summary>The terminal's latest cached input modes without acquiring the lock or calling native interop.</summary>
    public InputModes CachedInputModes => _cachedInputModes;

    /// <summary>The terminal's current input modes (cursor keys, mouse, bracketed paste ...).</summary>
    public InputModes ReadInputModes() => Read(c => c.ReadInputModes(), InputModes.Defaults);

    /// <summary>Everything on screen and in scrollback as plain text.</summary>
    public string ReadText() => Read(c => c.FormatPlainText(), string.Empty)!;

    /// <summary>Starts, changes or (empty text) ends the search for <paramref name="needle"/>; the first match is selected.</summary>
    public void SetSearch(string needle)
    {
        Use(c =>
        {
            if (needle.Length == 0) { c.EndSearch(); return; }
            var search = c.ActiveSearch ?? c.CreateSearch();
            if (search.Needle == needle) return;
            search.Needle = needle;
            search.Update();
            if (search.TotalMatches > 0 && search.SelectedIndex is null) search.Next();
        });
    }

    /// <summary>Selects the next match in reading order (further down); wraps around.</summary>
    public void SearchForward() => Use(c => { if (c.ActiveSearch is { TotalMatches: > 0 } s) s.Previous(); });

    /// <summary>Selects the previous match in reading order (further up); wraps around.</summary>
    public void SearchBackward() => Use(c => { if (c.ActiveSearch is { TotalMatches: > 0 } s) s.Next(); });

    /// <summary>Queues bytes for the process, in order with the terminal's own replies.</summary>
    public void Send(ReadOnlySpan<byte> bytes) => TrySend(bytes);

    /// <summary>Queues UTF-8 text for the process.</summary>
    public void Send(string text) => TrySend(text);

    /// <summary>Queues manual user bytes for the process and reports whether the live write queue accepted them.</summary>
    public bool TrySend(ReadOnlySpan<byte> bytes) => TrySendManual(bytes.ToArray(), ManualInputKind.Raw, 0);

    /// <summary>Queues manual user UTF-8 text for the process and reports whether the live write queue accepted them.</summary>
    public bool TrySend(string text) => TrySendManual(System.Text.Encoding.UTF8.GetBytes(text), ManualInputKind.Raw, 0);

    internal bool TrySendManual(ReadOnlySpan<byte> bytes, ManualInputKind kind, int charCount = 0) =>
        TrySendManual(bytes.ToArray(), kind, charCount);

    internal bool TrySendManual(string text, ManualInputKind kind, int charCount = 0) =>
        TrySendManual(System.Text.Encoding.UTF8.GetBytes(text), kind, charCount);

    private bool TrySendManual(byte[] data, ManualInputKind kind, int charCount)
    {
        var link = _link;
        if (_disposed || !_isRunning || link is null || link.Connection.Exited.IsCompleted) return false;
        lock (link.WriteGate)
        {
            if (_disposed || !_isRunning || !ReferenceEquals(_link, link) || link.Connection.Exited.IsCompleted) return false;
            if (link.CollabSubmissionHeld)
            {
                link.DeferredManualWrites.Add((data, kind, charCount));
                return true;
            }
            // While a held replay (or earlier deferred live manual write) is in flight in the PTY write queue,
            // account for new live keystrokes when the writer accepts them so draft and turn bookkeeping stay in
            // exact PTY wire order.
            var deferAccounting = link.HeldReplayInFlight || link.PendingLiveManualAccounting > 0;
            if (!link.Writes.Writer.TryWrite(new WriteItem(
                data,
                null,
                CancellationToken.None,
                applyManualOnAccept: deferAccounting,
                manualKind: kind,
                manualCharCount: charCount)))
            {
                return false;
            }
            if (deferAccounting)
            {
                link.PendingLiveManualAccounting++;
            }
            else
            {
                ApplyManualInputLocked(link, data, kind, charCount);
            }
            return true;
        }
    }

    private bool TrySendProtocolReply(ReadOnlySpan<byte> bytes)
    {
        var link = _link;
        if (_disposed || !_isRunning || link is null || link.Connection.Exited.IsCompleted) return false;
        return link.Writes.Writer.TryWrite(new WriteItem(bytes.ToArray(), null, CancellationToken.None));
    }

    /// <summary>Sends a collaboration control sequence (such as Esc interrupt) without recording manual user input.</summary>
    public bool TrySendCollabControl(string text, long expectedGeneration)
    {
        var link = _link;
        if (_disposed || !_isRunning || link is null || link.Generation != expectedGeneration || link.Connection.Exited.IsCompleted) return false;
        return link.Writes.Writer.TryWrite(new WriteItem(System.Text.Encoding.UTF8.GetBytes(text), null, CancellationToken.None));
    }

    /// <summary>
    /// Acquires an exclusive collaboration submission hold on <paramref name="expectedGeneration"/>. Refuses without
    /// clearing input if the user currently has an unsubmitted manual draft or an unresolved cancelled paste; holds
    /// concurrent manual input until the returned scope is disposed so manual keystrokes are never submitted by
    /// collaboration Enter.
    /// </summary>
    public bool TryBeginCollabSubmission(long expectedGeneration, out ICollabSubmissionScope? scope)
    {
        var link = _link;
        if (_disposed || !_isRunning || link is null || link.Generation != expectedGeneration || link.Connection.Exited.IsCompleted)
        {
            scope = null;
            return false;
        }
        lock (link.WriteGate)
        {
            if (_disposed || !_isRunning || !ReferenceEquals(_link, link) || link.Generation != expectedGeneration || link.Connection.Exited.IsCompleted)
            {
                scope = null;
                return false;
            }
            if (link.HasManualDraft || link.CollabSubmissionHeld || link.HasUnresolvedCancelledPaste || link.PendingLiveManualAccounting > 0)
            {
                scope = null;
                return false;
            }
            link.CollabSubmissionHeld = true;
            scope = new CollabSubmissionScope(this, link);
            return true;
        }
    }

    private void EndCollabSubmission(Link link, bool pasteLanded, bool committed)
    {
        var recoveryChanged = false;
        lock (link.WriteGate)
        {
            if (!link.CollabSubmissionHeld) return;
            link.CollabSubmissionHeld = false;
            var live = !_disposed && _isRunning && ReferenceEquals(_link, link) && !link.Connection.Exited.IsCompleted;
            if (live && pasteLanded && !committed)
            {
                // The cancelled bracketed paste may already sit in the child editor without a committed Enter, so
                // mark the prompt draft indeterminate as well as fencing further automated collaboration submissions.
                link.HasUnresolvedCancelledPaste = true;
                link.ManualDraftChars = -1;
                if (link.DeferredManualWrites.Count > 0)
                {
                    foreach (var entry in link.DeferredManualWrites)
                    {
                        link.HeldManualWrites.Add(new HeldEntry(entry.Data, entry.Kind, entry.CharCount));
                    }
                    link.DeferredManualWrites.Clear();
                }
                link.HasUncertainHeldWrite = false;
                link.RecoveryId = Interlocked.Increment(ref s_nextRecoveryId);
                recoveryChanged = true;
            }
            else
            {
                if (live && link.DeferredManualWrites.Count > 0)
                {
                    foreach (var entry in link.DeferredManualWrites)
                    {
                        if (link.Writes.Writer.TryWrite(new WriteItem(entry.Data, null, CancellationToken.None)))
                        {
                            ApplyManualInputLocked(link, entry.Data, entry.Kind, entry.CharCount);
                        }
                    }
                }
                link.DeferredManualWrites.Clear();
            }
        }
        if (recoveryChanged)
        {
            PendingInputRecoveryChanged?.Invoke();
        }
    }

    /// <summary>
    /// Returns the active host-side input recovery state when a bracketed collaboration paste was cancelled before
    /// commit on the live link, or null when input is clean or no link is running.
    /// </summary>
    public TerminalPendingInputRecovery? GetPendingInputRecovery()
    {
        var link = _link;
        if (_disposed || !_isRunning || link is null || link.Connection.Exited.IsCompleted) return null;
        lock (link.WriteGate)
        {
            if (_disposed || !_isRunning || !ReferenceEquals(_link, link) || link.Connection.Exited.IsCompleted) return null;
            if (!link.HasUnresolvedCancelledPaste && link.HeldManualWrites.Count == 0) return null;
            var bytes = ConcatHeldBytesLocked(link);
            var text = bytes.Length == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(bytes);
            return new TerminalPendingInputRecovery(
                link.HasUnresolvedCancelledPaste,
                link.HeldManualWrites.Count > 0,
                FormatHeldInputPreview(text),
                text,
                bytes)
            {
                RecoveryId = link.RecoveryId,
            };
        }
    }

    internal bool IsHeldReplayInFlight
    {
        get
        {
            var link = _link;
            if (_disposed || !_isRunning || link is null || link.Connection.Exited.IsCompleted) return false;
            lock (link.WriteGate)
            {
                if (_disposed || !_isRunning || !ReferenceEquals(_link, link) || link.Connection.Exited.IsCompleted) return false;
                return link.HeldReplayInFlight;
            }
        }
    }

    internal bool HasUncertainHeldWrite
    {
        get
        {
            var link = _link;
            if (_disposed || !_isRunning || link is null || link.Connection.Exited.IsCompleted) return false;
            lock (link.WriteGate)
            {
                if (_disposed || !_isRunning || !ReferenceEquals(_link, link) || link.Connection.Exited.IsCompleted) return false;
                return link.HasUncertainHeldWrite;
            }
        }
    }

    internal bool CanSendHeldManualInput
    {
        get
        {
            var link = _link;
            if (_disposed || !_isRunning || link is null || link.Connection.Exited.IsCompleted) return false;
            lock (link.WriteGate)
            {
                if (_disposed || !_isRunning || !ReferenceEquals(_link, link) || link.Connection.Exited.IsCompleted) return false;
                return !link.CollabSubmissionHeld
                    && !link.HeldReplayInFlight
                    && !link.HasUncertainHeldWrite
                    && link.HeldManualWrites.Count > 0;
            }
        }
    }

    /// <summary>
    /// Explicitly writes any held manual input batch to the live PTY in original order. Entries are removed only as
    /// the PTY writer accepts them so rejected writes keep unaccepted bytes available without duplicating accepted
    /// prefixes or blindly retrying uncertain writes. Clears the cancelled-paste fence only when all held entries are
    /// accepted and submit or cancel the prompt line.
    /// </summary>
    public bool SendHeldManualInput() => SendHeldManualInputCore(expectedRecoveryId: null);

    /// <summary>
    /// Overload of <see cref="SendHeldManualInput()"/> bound to a specific displayed recovery identity so stale UI
    /// actions cannot affect a newer recovery on the same session or a restarted generation.
    /// </summary>
    public bool SendHeldManualInput(long expectedRecoveryId) => SendHeldManualInputCore(expectedRecoveryId);

    private bool SendHeldManualInputCore(long? expectedRecoveryId)
    {
        var link = _link;
        if (_disposed || !_isRunning || link is null || link.Connection.Exited.IsCompleted) return false;
        HeldReplayState replay;
        lock (link.WriteGate)
        {
            if (_disposed || !_isRunning || !ReferenceEquals(_link, link) || link.Connection.Exited.IsCompleted) return false;
            if (link.CollabSubmissionHeld || link.HeldReplayInFlight || link.HasUncertainHeldWrite || link.HeldManualWrites.Count == 0) return false;
            if (expectedRecoveryId is { } id && link.RecoveryId != id) return false;

            var snapshot = link.HeldManualWrites.ToArray();
            replay = new HeldReplayState(snapshot.Length);
            var queued = 0;
            foreach (var entry in snapshot)
            {
                if (!link.Writes.Writer.TryWrite(new WriteItem(entry.Data, null, CancellationToken.None, entry, replay)))
                {
                    break;
                }
                queued++;
            }
            if (queued == 0)
            {
                return false;
            }
            if (queued < snapshot.Length)
            {
                replay.Remaining = queued;
                replay.AnyFailed = true;
            }
            link.HeldReplayInFlight = true;
        }

        // Wait briefly so synchronous/fast PTYs finish updating recovery state before returning without deadlocking
        // on slow or blocked connection writes.
        if (replay.Done.Task.Wait(millisecondsTimeout: 50))
        {
            return Volatile.Read(ref replay.AcceptedCount) > 0;
        }
        PendingInputRecoveryChanged?.Invoke();
        return true;
    }

    private bool TryBeginPreWriteManualAccounting(
        Link link,
        ReadOnlySpan<byte> bytes,
        ManualInputKind kind,
        int charCount,
        out ManualAccountingSnapshot snapshot)
    {
        lock (link.WriteGate)
        {
            if (_disposed || !_isRunning || !ReferenceEquals(_link, link) || link.Cancel.IsCancellationRequested || link.Connection.Exited.IsCompleted)
            {
                snapshot = default;
                return false;
            }
            link.InFlightFirstTurnOrder = 0;
            link.InFlightTurnCount = 0;
            link.InFlightConsumedCount = 0;
            snapshot = new ManualAccountingSnapshot
            {
                PrevDraftChars = link.ManualDraftChars,
                PrevInPaste = link.InManualBracketedPaste,
                DraftEpoch = link.ManualDraftEpoch,
            };
            snapshot.ResolvedLine = ApplyManualInputLocked(link, bytes, kind, charCount, provisional: true);
            return true;
        }
    }

    private static void CommitPreWriteManualAccountingLocked(Link link)
    {
        var remaining = link.InFlightTurnCount - link.InFlightConsumedCount;
        var startOrder = link.InFlightFirstTurnOrder + link.InFlightConsumedCount;
        link.InFlightFirstTurnOrder = 0;
        link.InFlightTurnCount = 0;
        link.InFlightConsumedCount = 0;
        if (remaining <= 0) return;

        if (remaining >= Link.MaxTrackedManualTurns)
        {
            link.EvictedManualTurns += link.PendingManualTurns.Count + (remaining - Link.MaxTrackedManualTurns);
            link.PendingManualTurns.Clear();
            var firstKept = startOrder + (remaining - Link.MaxTrackedManualTurns);
            for (var i = 0; i < Link.MaxTrackedManualTurns; i++)
            {
                link.PendingManualTurns.Enqueue(firstKept + i);
            }
            return;
        }

        while (link.PendingManualTurns.Count + remaining > Link.MaxTrackedManualTurns)
        {
            link.PendingManualTurns.Dequeue();
            link.EvictedManualTurns++;
        }
        for (var i = 0; i < remaining; i++)
        {
            link.PendingManualTurns.Enqueue(startOrder + i);
        }
    }

    private static void RollbackManualInputLocked(Link link, in ManualAccountingSnapshot snapshot)
    {
        if (link.ManualDraftEpoch == snapshot.DraftEpoch)
        {
            link.ManualDraftChars = snapshot.PrevDraftChars;
            link.InManualBracketedPaste = snapshot.PrevInPaste;
        }
        link.InFlightFirstTurnOrder = 0;
        link.InFlightTurnCount = 0;
        link.InFlightConsumedCount = 0;
    }

    private void OnDeferredLiveManualSettled(Link link, bool accepted, bool preApplied, in ManualAccountingSnapshot snapshot)
    {
        lock (link.WriteGate)
        {
            if (link.PendingLiveManualAccounting > 0)
            {
                link.PendingLiveManualAccounting--;
            }
            if (preApplied)
            {
                if (accepted && !_disposed && _isRunning && ReferenceEquals(_link, link))
                {
                    CommitPreWriteManualAccountingLocked(link);
                }
                else
                {
                    RollbackManualInputLocked(link, snapshot);
                }
            }
        }
    }

    private void OnHeldWriteSettled(
        Link link,
        HeldEntry entry,
        HeldReplayState replay,
        bool accepted,
        bool writeAttempted,
        bool preApplied,
        in ManualAccountingSnapshot snapshot)
    {
        var recoveryChanged = false;
        var batchFinished = false;
        lock (link.WriteGate)
        {
            var live = !_disposed && _isRunning && ReferenceEquals(_link, link) && !link.Connection.Exited.IsCompleted;
            var removedEntry = false;
            if (accepted)
            {
                if (link.HeldManualWrites.Remove(entry))
                {
                    removedEntry = true;
                    if (preApplied)
                    {
                        CommitPreWriteManualAccountingLocked(link);
                    }
                    if (snapshot.ResolvedLine)
                    {
                        replay.ResolvedLine = true;
                    }
                    Interlocked.Increment(ref replay.AcceptedCount);
                }
                else if (preApplied)
                {
                    RollbackManualInputLocked(link, snapshot);
                }
            }
            else
            {
                if (preApplied)
                {
                    RollbackManualInputLocked(link, snapshot);
                }
                replay.AnyFailed = true;
                if (writeAttempted && live)
                {
                    link.HasUncertainHeldWrite = true;
                }
            }

            replay.Remaining--;
            if (replay.Remaining <= 0)
            {
                batchFinished = true;
                link.HeldReplayInFlight = false;
                if (live && (link.HasUnresolvedCancelledPaste || link.HeldManualWrites.Count > 0))
                {
                    if (!replay.AnyFailed && link.HeldManualWrites.Count == 0 && replay.ResolvedLine)
                    {
                        link.HasUnresolvedCancelledPaste = false;
                        link.HasUncertainHeldWrite = false;
                        link.RecoveryId = 0;
                    }
                    else
                    {
                        link.RecoveryId = Interlocked.Increment(ref s_nextRecoveryId);
                    }
                    recoveryChanged = true;
                }
            }
            else if (removedEntry && live)
            {
                // Advance the displayed recovery identity and notify UI as soon as an individual prefix entry is
                // accepted so Copy/preview never expose already-written bytes while later entries remain in flight.
                link.RecoveryId = Interlocked.Increment(ref s_nextRecoveryId);
                recoveryChanged = true;
            }
        }
        if (batchFinished)
        {
            replay.Done.TrySetResult();
        }
        if (recoveryChanged)
        {
            PendingInputRecoveryChanged?.Invoke();
        }
    }

    /// <summary>
    /// Explicitly discards held manual input bytes without sending them to the PTY. Keeps the cancelled-paste fence
    /// active until the user also confirms the terminal input is clean or restarts the session.
    /// </summary>
    public bool DiscardHeldManualInput() => DiscardHeldManualInputCore(expectedRecoveryId: null);

    /// <summary>
    /// Overload of <see cref="DiscardHeldManualInput()"/> bound to a specific displayed recovery identity so stale UI
    /// actions cannot discard a newer recovery's held input.
    /// </summary>
    public bool DiscardHeldManualInput(long expectedRecoveryId) => DiscardHeldManualInputCore(expectedRecoveryId);

    private bool DiscardHeldManualInputCore(long? expectedRecoveryId)
    {
        var link = _link;
        if (_disposed || !_isRunning || link is null || link.Connection.Exited.IsCompleted) return false;
        var changed = false;
        lock (link.WriteGate)
        {
            if (_disposed || !_isRunning || !ReferenceEquals(_link, link) || link.Connection.Exited.IsCompleted) return false;
            if (link.HeldReplayInFlight || link.HeldManualWrites.Count == 0) return false;
            if (expectedRecoveryId is { } id && link.RecoveryId != id) return false;
            link.HeldManualWrites.Clear();
            link.HasUncertainHeldWrite = false;
            link.RecoveryId = Interlocked.Increment(ref s_nextRecoveryId);
            changed = true;
        }
        if (changed) PendingInputRecoveryChanged?.Invoke();
        return changed;
    }

    /// <summary>
    /// Explicitly clears the cancelled-paste fence and reconciles tracked manual draft state with the user's
    /// confirmation so automated collaboration submissions may resume on this link. Refuses while held manual input
    /// still exists so buffered user bytes are never silently dropped.
    /// </summary>
    public bool MarkInputClean() => MarkInputCleanCore(expectedRecoveryId: null);

    /// <summary>
    /// Overload of <see cref="MarkInputClean()"/> bound to a specific displayed recovery identity so stale UI actions
    /// cannot clear a newer recovery's fence.
    /// </summary>
    public bool MarkInputClean(long expectedRecoveryId) => MarkInputCleanCore(expectedRecoveryId);

    private bool MarkInputCleanCore(long? expectedRecoveryId)
    {
        var link = _link;
        if (_disposed || !_isRunning || link is null || link.Connection.Exited.IsCompleted) return false;
        var changed = false;
        lock (link.WriteGate)
        {
            if (_disposed || !_isRunning || !ReferenceEquals(_link, link) || link.Connection.Exited.IsCompleted) return false;
            if (!link.HasUnresolvedCancelledPaste || link.HeldManualWrites.Count > 0) return false;
            if (expectedRecoveryId is { } id && link.RecoveryId != id) return false;
            link.HasUnresolvedCancelledPaste = false;
            link.ManualDraftChars = 0;
            link.InManualBracketedPaste = false;
            link.ManualDraftEpoch++;
            link.RecoveryId = 0;
            changed = true;
        }
        if (changed) PendingInputRecoveryChanged?.Invoke();
        return changed;
    }

    private static byte[] ConcatHeldBytesLocked(Link link)
    {
        if (link.HeldManualWrites.Count == 0) return [];
        var total = 0;
        foreach (var entry in link.HeldManualWrites) total += entry.Data.Length;
        var result = new byte[total];
        var offset = 0;
        foreach (var entry in link.HeldManualWrites)
        {
            Buffer.BlockCopy(entry.Data, 0, result, offset, entry.Data.Length);
            offset += entry.Data.Length;
        }
        return result;
    }

    private static string FormatHeldInputPreview(string text)
    {
        if (text.Length == 0) return string.Empty;
        const int maxChars = 80;
        var sb = new System.Text.StringBuilder(Math.Min(text.Length, maxChars + 1));
        foreach (var ch in text)
        {
            if (sb.Length >= maxChars)
            {
                sb.Append('…');
                break;
            }
            switch (ch)
            {
                case '\r':
                case '\n':
                    sb.Append('↵');
                    break;
                case '\t':
                    sb.Append('⇥');
                    break;
                case '\x1b':
                    sb.Append('␛');
                    break;
                default:
                    sb.Append(ch < 0x20 || ch == 0x7F ? '·' : ch);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Queues UTF-8 text for the process on <paramref name="expectedGeneration"/> and outputs a task that resolves to
    /// true only when the underlying PTY connection accepts the write on the same live link.
    /// </summary>
    public bool TryEnqueueWrite(
        string text,
        long expectedGeneration,
        CancellationToken callerCancellation,
        out Task<bool> completion) =>
        TryEnqueueCollabWrite(text, expectedGeneration, callerCancellation, assignTurnOrder: false, out _, out completion);

    /// <summary>
    /// Queues collaboration UTF-8 text on <paramref name="expectedGeneration"/> and optionally assigns a unified turn
    /// order number shared with manual prompt submissions on the same link.
    /// </summary>
    public bool TryEnqueueCollabWrite(
        string text,
        long expectedGeneration,
        CancellationToken callerCancellation,
        bool assignTurnOrder,
        out long turnOrder,
        out Task<bool> completion)
    {
        var link = _link;
        if (_disposed || !_isRunning || link is null || link.Generation != expectedGeneration || link.Connection.Exited.IsCompleted || callerCancellation.IsCancellationRequested)
        {
            turnOrder = 0;
            completion = Task.FromResult(false);
            return false;
        }
        lock (link.WriteGate)
        {
            if (_disposed || !_isRunning || !ReferenceEquals(_link, link) || link.Generation != expectedGeneration || link.Connection.Exited.IsCompleted || callerCancellation.IsCancellationRequested)
            {
                turnOrder = 0;
                completion = Task.FromResult(false);
                return false;
            }
            if ((!link.CollabSubmissionHeld && link.HasManualDraft) || link.HasUnresolvedCancelledPaste || link.PendingLiveManualAccounting > 0)
            {
                turnOrder = 0;
                completion = Task.FromResult(false);
                return false;
            }
            var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var item = new WriteItem(System.Text.Encoding.UTF8.GetBytes(text), ack, callerCancellation);
            if (!link.Writes.Writer.TryWrite(item))
            {
                item.CancelDropped();
                turnOrder = 0;
                completion = Task.FromResult(false);
                return false;
            }
            turnOrder = assignTurnOrder ? ++link.NextTurnOrder : 0;
            completion = ack.Task;
            return true;
        }
    }

    /// <summary>Drains pending manual turn sequence numbers recorded on <paramref name="expectedGeneration"/>.</summary>
    public void DrainManualTurns(long expectedGeneration, ref List<long>? target, out long evictedCount)
    {
        var link = _link;
        if (link is null || link.Generation != expectedGeneration)
        {
            evictedCount = 0;
            return;
        }
        lock (link.WriteGate)
        {
            if (link.Generation != expectedGeneration)
            {
                evictedCount = 0;
                return;
            }
            evictedCount = link.EvictedManualTurns;
            link.EvictedManualTurns = 0;
            if (link.PendingManualTurns.Count == 0) return;
            target ??= new List<long>(link.PendingManualTurns.Count);
            while (link.PendingManualTurns.Count > 0)
            {
                target.Add(link.PendingManualTurns.Dequeue());
            }
        }
    }

    /// <summary>Consumes one leading manual turn recorded on the current link when a Done signal arrives without a collab bridge.</summary>
    public bool TryConsumeLeadingManualTurn()
    {
        var link = _link;
        if (link is null) return false;
        lock (link.WriteGate)
        {
            if (!ReferenceEquals(_link, link)) return false;
            return TryConsumeLeadingManualTurnLocked(link);
        }
    }

    /// <summary>Consumes one leading committed or provisional manual turn on <paramref name="expectedGeneration"/> when a Done signal arrives with no earlier pending turns.</summary>
    public bool TryConsumeLeadingManualTurn(long expectedGeneration)
    {
        var link = _link;
        if (link is null || link.Generation != expectedGeneration) return false;
        lock (link.WriteGate)
        {
            if (!ReferenceEquals(_link, link) || link.Generation != expectedGeneration) return false;
            return TryConsumeLeadingManualTurnLocked(link);
        }
    }

    private static bool TryConsumeLeadingManualTurnLocked(Link link)
    {
        if (link.EvictedManualTurns > 0)
        {
            link.EvictedManualTurns--;
            return true;
        }
        if (link.PendingManualTurns.Count > 0)
        {
            link.PendingManualTurns.Dequeue();
            return true;
        }
        if (link.InFlightConsumedCount < link.InFlightTurnCount)
        {
            link.InFlightConsumedCount++;
            return true;
        }
        return false;
    }

    private static void EnqueueManualTurnLocked(Link link, bool provisional)
    {
        var order = ++link.NextTurnOrder;
        if (provisional)
        {
            if (link.InFlightTurnCount == 0)
            {
                link.InFlightFirstTurnOrder = order;
            }
            link.InFlightTurnCount++;
            return;
        }
        while (link.PendingManualTurns.Count >= Link.MaxTrackedManualTurns)
        {
            link.PendingManualTurns.Dequeue();
            link.EvictedManualTurns++;
        }
        link.PendingManualTurns.Enqueue(order);
    }

    private static bool ApplyManualInputLocked(Link link, ReadOnlySpan<byte> bytes, ManualInputKind kind, int charCount, bool provisional = false)
    {
        switch (kind)
        {
            case ManualInputKind.NonEditing:
                return false;
            case ManualInputKind.Printable:
                if (charCount > 0)
                {
                    link.ManualDraftChars = link.ManualDraftChars >= 0 ? link.ManualDraftChars + charCount : -1;
                }
                return false;
            case ManualInputKind.Backspace:
                if (link.ManualDraftChars > 0)
                {
                    link.ManualDraftChars--;
                }
                return false;
            case ManualInputKind.Submit:
                if (link.ManualDraftChars != 0)
                {
                    link.ManualDraftChars = 0;
                    EnqueueManualTurnLocked(link, provisional);
                }
                return true;
            case ManualInputKind.CancelLine:
                link.ManualDraftChars = 0;
                return true;
            case ManualInputKind.KillToStart:
                if (link.ManualDraftChars > 0)
                {
                    link.ManualDraftChars = 0;
                }
                return false;
            case ManualInputKind.CursorOrEdit:
                if (link.ManualDraftChars > 0)
                {
                    link.ManualDraftChars = -1;
                }
                return false;
            case ManualInputKind.UnobservableEdit:
                link.ManualDraftChars = -1;
                return false;
            case ManualInputKind.Raw:
            default:
                return RecordRawManualInputLocked(link, bytes, provisional);
        }
    }

    private static bool RecordRawManualInputLocked(Link link, ReadOnlySpan<byte> bytes, bool provisional)
    {
        if (bytes.IsEmpty) return false;
        var resolvedLine = false;
        var i = 0;
        while (i < bytes.Length)
        {
            if (link.InManualBracketedPaste)
            {
                if (MatchesAscii(bytes, i, "\x1b[201~"u8))
                {
                    link.InManualBracketedPaste = false;
                    i += 6;
                    continue;
                }
                link.ManualDraftChars = -1;
                i++;
                continue;
            }

            var b = bytes[i];
            if (b == 0x1B)
            {
                if (MatchesAscii(bytes, i, "\x1b[200~"u8))
                {
                    link.InManualBracketedPaste = true;
                    link.ManualDraftChars = -1;
                    i += 6;
                    continue;
                }
                // Focus in/out (ESC [ I / ESC [ O)
                if (i + 2 < bytes.Length && bytes[i + 1] == (byte)'[' && (bytes[i + 2] == (byte)'I' || bytes[i + 2] == (byte)'O'))
                {
                    i += 3;
                    continue;
                }
                // X10 mouse report (ESC [ M + 3 bytes)
                if (i + 5 < bytes.Length && bytes[i + 1] == (byte)'[' && bytes[i + 2] == (byte)'M')
                {
                    i += 6;
                    continue;
                }
                // CSI sequence (ESC [ ... final byte 0x40..0x7E)
                if (i + 1 < bytes.Length && bytes[i + 1] == (byte)'[')
                {
                    var end = i + 2;
                    while (end < bytes.Length && (bytes[end] < 0x40 || bytes[end] > 0x7E)) end++;
                    if (end < bytes.Length)
                    {
                        var seq = bytes[i..(end + 1)];
                        // SGR mouse reports (ESC [ < ... M/m) are non-editing pointer traffic.
                        if (seq.Length >= 4 && seq[2] == (byte)'<' && (seq[^1] == (byte)'M' || seq[^1] == (byte)'m'))
                        {
                            i = end + 1;
                            continue;
                        }
                        if (seq.SequenceEqual("\x1b[Z"u8))
                        {
                            link.ManualDraftChars = -1;
                            i = end + 1;
                            continue;
                        }
                        if (TryParseKittyCsiU(seq, out var cp, out var mods, out var eventType))
                        {
                            if (eventType != 3 && ApplyKittyCsiULocked(link, cp, mods, provisional))
                            {
                                resolvedLine = true;
                            }
                            i = end + 1;
                            continue;
                        }
                        if (link.ManualDraftChars > 0)
                        {
                            link.ManualDraftChars = -1;
                        }
                        i = end + 1;
                        continue;
                    }
                }
                // Alt+Backspace (ESC DEL) or readline word motion (ESC b / ESC f)
                if (i + 1 < bytes.Length && (bytes[i + 1] == 0x7F || bytes[i + 1] == (byte)'b' || bytes[i + 1] == (byte)'f'))
                {
                    if (link.ManualDraftChars > 0)
                    {
                        link.ManualDraftChars = -1;
                    }
                    i += 2;
                    continue;
                }
                i++;
                continue;
            }

            if (b == (byte)'\r')
            {
                if (link.ManualDraftChars != 0)
                {
                    link.ManualDraftChars = 0;
                    EnqueueManualTurnLocked(link, provisional);
                }
                resolvedLine = true;
                i++;
                continue;
            }

            if (b == 0x7F || b == 0x08)
            {
                if (link.ManualDraftChars > 0)
                {
                    link.ManualDraftChars--;
                }
                i++;
                continue;
            }

            if (b == 0x03)
            {
                link.ManualDraftChars = 0;
                resolvedLine = true;
                i++;
                continue;
            }

            if (b == 0x15)
            {
                if (link.ManualDraftChars > 0)
                {
                    link.ManualDraftChars = 0;
                }
                i++;
                continue;
            }

            if (b == (byte)'\n' || b == (byte)'\t')
            {
                link.ManualDraftChars = -1;
                i++;
                continue;
            }

            if (b == 0x04 || b == 0x17 || b == 0x0B || b == 0x01 || b == 0x05)
            {
                if (link.ManualDraftChars > 0)
                {
                    link.ManualDraftChars = -1;
                }
                i++;
                continue;
            }

            if (b >= 0x80)
            {
                var status = System.Text.Rune.DecodeFromUtf8(bytes[i..], out var rune, out var consumed);
                if (status == System.Buffers.OperationStatus.Done && consumed > 0)
                {
                    if (!System.Text.Rune.IsControl(rune) && link.ManualDraftChars >= 0)
                    {
                        link.ManualDraftChars++;
                    }
                    i += consumed;
                    continue;
                }
                link.ManualDraftChars = -1;
                i++;
                continue;
            }

            if (b >= 0x20 && b != 0x7F)
            {
                if (link.ManualDraftChars >= 0)
                {
                    link.ManualDraftChars++;
                }
            }
            i++;
        }
        return resolvedLine;
    }

    private static bool ApplyKittyCsiULocked(Link link, int codepoint, int modifiers, bool provisional)
    {
        // Kitty modifiers are encoded as 1 + bitmask: Shift=1, Alt=2, Ctrl=4, Super=8.
        var mask = Math.Max(0, modifiers - 1);
        const int shiftBit = 1;
        const int altBit = 2;
        const int ctrlBit = 4;
        const int superBit = 8;
        var relevant = mask & (shiftBit | altBit | ctrlBit | superBit);

        if (codepoint == 27 && relevant == 0)
        {
            return false;
        }
        if (codepoint == 9 && (relevant == 0 || relevant == shiftBit))
        {
            link.ManualDraftChars = -1;
            return false;
        }
        if (codepoint is 13 or 57414)
        {
            if (relevant == 0)
            {
                if (link.ManualDraftChars != 0)
                {
                    link.ManualDraftChars = 0;
                    EnqueueManualTurnLocked(link, provisional);
                }
                return true;
            }
            if (relevant == shiftBit)
            {
                link.ManualDraftChars = -1;
            }
            else if (link.ManualDraftChars > 0)
            {
                link.ManualDraftChars = -1;
            }
            return false;
        }
        if (codepoint == 127)
        {
            if (relevant == 0)
            {
                if (link.ManualDraftChars > 0) link.ManualDraftChars--;
            }
            else if (relevant == superBit)
            {
                if (link.ManualDraftChars > 0) link.ManualDraftChars = 0;
            }
            else if (link.ManualDraftChars > 0)
            {
                link.ManualDraftChars = -1;
            }
            return false;
        }
        if (relevant == ctrlBit)
        {
            if (codepoint is 'c' or 'C')
            {
                link.ManualDraftChars = 0;
                return true;
            }
            if (codepoint is 'u' or 'U')
            {
                if (link.ManualDraftChars > 0) link.ManualDraftChars = 0;
                return false;
            }
            if (codepoint is 'd' or 'D')
            {
                if (link.ManualDraftChars > 0) link.ManualDraftChars = -1;
                return false;
            }
            if (codepoint is 'j' or 'J')
            {
                link.ManualDraftChars = -1;
                return false;
            }
        }
        if (codepoint is >= 0xE000 and <= 0xF8FF)
        {
            // Kitty functional keys occupy U+E000..U+F8FF (modifiers, locks, F13-F35, media, keypad).
            if (codepoint is (>= 57399 and <= 57413) or 57415 or 57416)
            {
                if ((relevant & (altBit | ctrlBit | superBit)) == 0)
                {
                    if (link.ManualDraftChars >= 0) link.ManualDraftChars++;
                }
                else if (link.ManualDraftChars > 0)
                {
                    link.ManualDraftChars = -1;
                }
                return false;
            }
            if (codepoint is >= 57417 and <= 57427)
            {
                if (link.ManualDraftChars > 0) link.ManualDraftChars = -1;
                return false;
            }
            return false;
        }
        if ((relevant & (altBit | ctrlBit | superBit)) == 0
            && codepoint >= 0x20
            && codepoint != 0x7F
            && codepoint <= 0x10FFFF
            && codepoint is not (>= 0x80 and <= 0x9F)
            && codepoint is not (>= 0xD800 and <= 0xDFFF))
        {
            if (link.ManualDraftChars >= 0) link.ManualDraftChars++;
            return false;
        }
        if (link.ManualDraftChars > 0)
        {
            link.ManualDraftChars = -1;
        }
        return false;
    }

    private static bool TryParseKittyCsiU(ReadOnlySpan<byte> seq, out int codepoint, out int modifiers, out int eventType)
    {
        codepoint = 0;
        modifiers = 1;
        eventType = 1;
        if (seq.Length < 4 || seq[0] != 0x1B || seq[1] != (byte)'[' || seq[^1] != (byte)'u') return false;
        var body = seq[2..^1];
        if (body.IsEmpty) return false;

        var semi = body.IndexOf((byte)';');
        var keyField = semi < 0 ? body : body[..semi];
        var keyColon = keyField.IndexOf((byte)':');
        var cpSlice = keyColon < 0 ? keyField : keyField[..keyColon];
        if (!TryParseAsciiInt(cpSlice, out codepoint)) return false;

        if (semi >= 0)
        {
            var rest = body[(semi + 1)..];
            var nextSemi = rest.IndexOf((byte)';');
            var modField = nextSemi < 0 ? rest : rest[..nextSemi];
            if (!modField.IsEmpty)
            {
                var modColon = modField.IndexOf((byte)':');
                var modSlice = modColon < 0 ? modField : modField[..modColon];
                if (!modSlice.IsEmpty && !TryParseAsciiInt(modSlice, out modifiers)) return false;
                if (modColon >= 0)
                {
                    var evtSlice = modField[(modColon + 1)..];
                    if (!evtSlice.IsEmpty && !TryParseAsciiInt(evtSlice, out eventType)) return false;
                }
            }
        }
        return true;
    }

    private static bool TryParseAsciiInt(ReadOnlySpan<byte> span, out int value)
    {
        value = 0;
        if (span.IsEmpty) return false;
        foreach (var ch in span)
        {
            if (ch < (byte)'0' || ch > (byte)'9') return false;
            if (value > (int.MaxValue - 9) / 10) return false;
            value = (value * 10) + (ch - (byte)'0');
        }
        return true;
    }

    private static bool MatchesAscii(ReadOnlySpan<byte> bytes, int index, ReadOnlySpan<byte> pattern) =>
        index + pattern.Length <= bytes.Length && bytes.Slice(index, pattern.Length).SequenceEqual(pattern);

    /// <summary>Changes the grid for this app's own view (see <see cref="Resize(TerminalSize, object?)"/>).</summary>
    public bool Resize(TerminalSize size) => Resize(size, null);

    /// <summary>
    /// Changes the grid on behalf of <paramref name="owner"/>: the core first, then the pty (with pixel size). A terminal
    /// has one size and one view decides it (<see cref="SizeOwner"/>); a resize from any other view is not applied and
    /// returns false. A mirror only asks its connection: its grid follows the terminal it mirrors.
    /// </summary>
    public bool Resize(TerminalSize size, object? owner)
    {
        if (size.Columns < 1 || size.Rows < 1) return false;
        Link? link;
        lock (_gate)
        {
            if (_core is null) return false;
            if (IsMirror)
            {
                if (size == _requested) return true;
                _requested = size;
            }
            else
            {
                if (!ReferenceEquals(owner, _sizeOwner)) return false;
                if (size == _size) return true;
                _size = size;
                _core.Resize(size);
                _history?.AppendResize(_head, size);
                foreach (var tap in _taps) tap.OnResize(_head, size);
                _head++;
            }
            link = _link;
        }
        if (link is not null)
            _ = link.Connection.ResizeAsync(size.Columns, size.Rows, size.PixelWidth, size.PixelHeight).AsTask();
        RequestFlush();
        return true;
    }

    /// <summary>
    /// The view whose size the terminal has: null is the view of this app's own window, anything else stands for a
    /// view somewhere else (design/12-远程操作.md §6.5). The view that was last given the say keeps it until another
    /// claims it.
    /// </summary>
    public object? SizeOwner
    {
        get { lock (_gate) return _sizeOwner; }
    }

    /// <summary>The terminal's current grid.</summary>
    public TerminalSize Size
    {
        get { lock (_gate) return _size; }
    }

    /// <summary>Another view decides the size from now on, or the one that did let go. Raised on the thread that caused it.</summary>
    public event Action? SizeOwnerChanged;

    /// <summary>Makes <paramref name="owner"/> the view that decides the size, and gives the terminal that view's <paramref name="size"/>.</summary>
    public void ClaimSize(object? owner, TerminalSize size)
    {
        bool changed;
        lock (_gate)
        {
            changed = !ReferenceEquals(owner, _sizeOwner);
            if (owner is not null)
            {
                _claims.Remove(owner);
                _claims.Add(owner);
            }
            _sizeOwner = owner;
        }
        Resize(size, owner);
        if (changed) SizeOwnerChanged?.Invoke();
    }

    /// <summary>
    /// The view <paramref name="owner"/> is gone. When it had the say, the say goes to the view that had it before and
    /// is still there, or to this app's own view; whoever gets it applies its size again (<see cref="SizeOwnerChanged"/>).
    /// </summary>
    public void ReleaseSize(object owner)
    {
        lock (_gate)
        {
            _claims.Remove(owner);
            if (!ReferenceEquals(owner, _sizeOwner)) return;
            _sizeOwner = _claims.Count > 0 ? _claims[^1] : null;
        }
        SizeOwnerChanged?.Invoke();
    }

    /// <summary>Changes the default colours. Programs that ask for them get the new ones.</summary>
    public void SetPalette(TerminalPalette palette, ColorScheme scheme)
    {
        lock (_gate)
        {
            (_initialPalette, _initialScheme) = (palette, scheme);
            _core?.SetDefaultColors(palette, scheme);
        }
        ForceFlush();
    }

    private TerminalCoreOptions Options(TerminalSize size) => new(size)
    {
        ScrollbackMaxLines = _scrollbackLines,
        ScrollbackMaxBytes = MaxScrollbackBytes,
        ContinuationMaxBytes = IsMirror ? 0 : 256 * 1024,
        Replies = new TerminalReplyProfile("Octopus", AdvertiseOsc52: false, _initialScheme),
    };

    // A mirror's connection says, at exact places in the output, what else happened to the terminal it mirrors.
    // Called by the pump with the lock held.
    private void ApplyMirrorEvent(ITerminalMirrorConnection mirror)
    {
        if (_core is not null && mirror.TakeEvent() is { } change)
        {
            switch (change.Kind)
            {
                case TerminalMirrorEventKind.Resize when change.Columns > 0 && change.Rows > 0:
                    _size = new TerminalSize(change.Columns, change.Rows, _size.CellWidth, _size.CellHeight);
                    _core.Resize(_size);
                    break;
                case TerminalMirrorEventKind.Reset:
                    _core.Reset();
                    _core.SetDefaultColors(_initialPalette, _initialScheme);
                    _cachedInputModes = _core.ReadInputModes();
                    break;
                case TerminalMirrorEventKind.Snapshot when change.Snapshot is { } snapshot:
                    // The whole terminal as the owning side has it, in place of this one; the colors stay this side's.
                    var restored = TerminalCore.Restore(snapshot, Options(_size), this);
                    _core.Dispose();
                    _core = restored;
                    _size = restored.Size;
                    _core.SetDefaultColors(_initialPalette, _initialScheme);
                    _cachedInputModes = _core.ReadInputModes();
                    _dirtyWhileHidden = true;
                    break;
            }
        }
    }

    /// <summary>Takes a full frame at the next opportunity (after the view became visible or its metrics changed).</summary>
    public void ForceFlush()
    {
        _dirtyWhileHidden = true;
        RequestFlush();
    }

    /// <summary>Name of the foreground process in the pty, or null.</summary>
    public async ValueTask<string?> GetForegroundProcessNameAsync()
    {
        var link = _link;
        return link is null || !IsRunning ? null : await link.Connection.GetForegroundProcessNameAsync().ConfigureAwait(false);
    }

    /// <summary>Sends hangup, waits up to two seconds, kills, and frees the core.</summary>
    public ValueTask DisposeAsync()
    {
        Task disposeTask;
        lock (_gate)
        {
            if (_disposeTask is { } existing)
            {
                return new ValueTask(existing);
            }
            _disposed = true;
            Interlocked.Increment(ref _lifecycleEpoch);
            var coalesce = _coalesceTimer;
            _coalesceTimer = null;
            var hold = _renderHoldTimer;
            _renderHoldTimer = null;
            disposeTask = DisposeCoreAsync(coalesce, hold);
            _disposeTask = disposeTask;
        }
        return new ValueTask(disposeTask);
    }

    private async Task DisposeCoreAsync(Timer? coalesce, Timer? hold)
    {
        coalesce?.Dispose();
        hold?.Dispose();
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopLinkAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _core?.Dispose();
                _core = null;
                _history = null;
                _taps.Clear();
                _claims.Clear();
                _sizeOwner = null;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
        _isVisible = static () => false;
        FrameReady = null;
        TitleChanged = null;
        Bell = null;
        Exited = null;
        LinkExited = null;
        LinkStopped = null;
        SizeOwnerChanged = null;
        PendingInputRecoveryChanged = null;
    }

    private bool StartLink(bool allowNullLaunch, long? expectedEpoch, out ITerminalConnection? abortedConnection)
    {
        abortedConnection = null;
        lock (_gate)
        {
            if (_disposed || _core is null || (expectedEpoch is { } ep && Volatile.Read(ref _lifecycleEpoch) != ep))
            {
                return false;
            }
        }

        var generation = Interlocked.Increment(ref _generation);
        var linkId = Guid.NewGuid().ToString("N");
        var startedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var launch = _launchFactory(linkId);
        if (launch is null)
        {
            if (allowNullLaunch) return false;
            throw new InvalidOperationException("Terminal launch could not be planned.");
        }

        TerminalSize size;
        lock (_gate)
        {
            if (_disposed || _core is null || (expectedEpoch is { } ep && Volatile.Read(ref _lifecycleEpoch) != ep))
            {
                launch.Dispose();
                return false;
            }
            size = _size;
        }

        ITerminalConnection connection;
        try
        {
            connection = _factory.Start(WithSignalLinkId(launch.Spec, linkId), size.Columns, size.Rows);
        }
        catch
        {
            launch.Dispose();
            throw;
        }

        var link = new Link
        {
            Generation = generation,
            LinkId = linkId,
            StartedAtMs = startedAtMs,
            Connection = connection,
            Launch = launch,
        };
        bool aborted;
        lock (_gate)
        {
            aborted = _disposed
                || _core is null
                || _link is not null
                || (expectedEpoch is { } ep && Volatile.Read(ref _lifecycleEpoch) != ep);
            if (!aborted)
            {
                _spec = launch.Spec;
                _link = link;
                _lastLinkId = linkId;
                _startedLinkIds.Add(linkId);
            }
        }
        if (aborted)
        {
            link.ReleaseLaunch();
            abortedConnection = connection;
            return false;
        }
        IsRunning = true;
        // Release this process generation's lease as soon as the native process exits, independently of output drain.
        _ = connection.ProcessExited.ContinueWith(
            static (t, state) =>
            {
                _ = t.Exception;
                ((Link)state!).ReleaseLaunch();
            },
            link,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _ = Task.Run(() => WriteLoopAsync(link));
        _ = Task.Run(() => PumpAsync(link));
        _ = connection.ResizeAsync(size.Columns, size.Rows, size.PixelWidth, size.PixelHeight).AsTask();
        return true;
    }

    private static LaunchSpec WithSignalLinkId(LaunchSpec spec, string linkId)
    {
        if (!spec.Environment.ContainsKey("OCTOPUS_SIGNAL_ENDPOINT") && !spec.Environment.ContainsKey("OCTOPUS_ITEM_ID"))
        {
            return spec;
        }
        var env = new Dictionary<string, string>(spec.Environment, StringComparer.Ordinal)
        {
            ["OCTOPUS_SIGNAL_LINK_ID"] = linkId,
        };
        return spec with { Environment = env };
    }

    private async Task StopLinkAsync()
    {
        Link? link;
        lock (_gate)
        {
            link = _link;
            _link = null;
        }
        IsRunning = false;
        if (link is null) return;
        var wasAlreadyExited = link.Connection.Exited.IsCompleted;
        link.StopRequested = !wasAlreadyExited;
        var hadRecovery = false;
        lock (link.WriteGate)
        {
            hadRecovery = link.HasUnresolvedCancelledPaste || link.HeldManualWrites.Count > 0;
            link.HasUnresolvedCancelledPaste = false;
            link.HeldReplayInFlight = false;
            link.HasUncertainHeldWrite = false;
            link.PendingLiveManualAccounting = 0;
            link.InFlightFirstTurnOrder = 0;
            link.InFlightTurnCount = 0;
            link.InFlightConsumedCount = 0;
            link.RecoveryId = 0;
            link.HeldManualWrites.Clear();
            link.DeferredManualWrites.Clear();
        }
        if (hadRecovery) PendingInputRecoveryChanged?.Invoke();
        link.ReleaseLaunch();
        link.Writes.Writer.TryComplete();
        try { link.Cancel.Cancel(); } catch (ObjectDisposedException) { }
        try { await link.Connection.DisposeAsync().ConfigureAwait(false); } catch { }
        link.Cancel.Dispose();
        if (!wasAlreadyExited)
        {
            LinkStopped?.Invoke(link.Generation, link.LinkId);
        }
    }

    private async Task WriteLoopAsync(Link link)
    {
        try
        {
            await foreach (var item in link.Writes.Reader.ReadAllAsync(link.Cancel.Token).ConfigureAwait(false))
            {
                var hasHeldEntry = item.HeldEntry is not null && item.HeldReplay is not null;
                if (!item.TryBeginWrite())
                {
                    if (hasHeldEntry)
                    {
                        OnHeldWriteSettled(link, item.HeldEntry!, item.HeldReplay!, accepted: false, writeAttempted: false, preApplied: false, default);
                    }
                    else if (item.ApplyManualOnAccept)
                    {
                        OnDeferredLiveManualSettled(link, accepted: false, preApplied: false, default);
                    }
                    continue;
                }
                if (!ReferenceEquals(_link, link) || !_isRunning || link.Cancel.IsCancellationRequested || link.Connection.Exited.IsCompleted)
                {
                    item.Ack?.TrySetResult(false);
                    if (hasHeldEntry)
                    {
                        OnHeldWriteSettled(link, item.HeldEntry!, item.HeldReplay!, accepted: false, writeAttempted: false, preApplied: false, default);
                    }
                    else if (item.ApplyManualOnAccept)
                    {
                        OnDeferredLiveManualSettled(link, accepted: false, preApplied: false, default);
                    }
                    break;
                }
                // Apply manual draft/turn accounting in FIFO wire order immediately before dispatching bytes to the
                // PTY so an early Done signal arriving before WriteAsync's continuation runs already sees the manual
                // turn slot, while failed/refused writes roll back the snapshot without leaving a phantom turn.
                var preApplied = false;
                ManualAccountingSnapshot preWriteSnapshot = default;
                if (hasHeldEntry)
                {
                    if (!TryBeginPreWriteManualAccounting(link, item.HeldEntry!.Data, item.HeldEntry.Kind, item.HeldEntry.CharCount, out preWriteSnapshot))
                    {
                        item.Ack?.TrySetResult(false);
                        OnHeldWriteSettled(link, item.HeldEntry, item.HeldReplay!, accepted: false, writeAttempted: false, preApplied: false, default);
                        break;
                    }
                    preApplied = true;
                }
                else if (item.ApplyManualOnAccept)
                {
                    if (!TryBeginPreWriteManualAccounting(link, item.Bytes, item.ManualKind, item.ManualCharCount, out preWriteSnapshot))
                    {
                        item.Ack?.TrySetResult(false);
                        OnDeferredLiveManualSettled(link, accepted: false, preApplied: false, default);
                        break;
                    }
                    preApplied = true;
                }
                try
                {
                    await link.Connection.WriteAsync(item.Bytes, link.Cancel.Token).ConfigureAwait(false);
                    var stillLive = ReferenceEquals(_link, link) && _isRunning && !link.Cancel.IsCancellationRequested;
                    item.Ack?.TrySetResult(stillLive);
                    if (hasHeldEntry)
                    {
                        OnHeldWriteSettled(link, item.HeldEntry!, item.HeldReplay!, accepted: stillLive, writeAttempted: true, preApplied, preWriteSnapshot);
                    }
                    else if (item.ApplyManualOnAccept)
                    {
                        OnDeferredLiveManualSettled(link, accepted: stillLive, preApplied, preWriteSnapshot);
                    }
                    if (!stillLive) break;
                }
                catch
                {
                    item.Ack?.TrySetResult(false);
                    if (hasHeldEntry)
                    {
                        OnHeldWriteSettled(link, item.HeldEntry!, item.HeldReplay!, accepted: false, writeAttempted: true, preApplied, preWriteSnapshot);
                    }
                    else if (item.ApplyManualOnAccept)
                    {
                        OnDeferredLiveManualSettled(link, accepted: false, preApplied, preWriteSnapshot);
                    }
                    throw;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch { /* the process is gone; its exit is reported by the pump */ }
        finally
        {
            link.Writes.Writer.TryComplete();
            while (link.Writes.Reader.TryRead(out var dropped))
            {
                dropped.CancelDropped();
                if (dropped.HeldEntry is not null && dropped.HeldReplay is not null)
                {
                    OnHeldWriteSettled(link, dropped.HeldEntry, dropped.HeldReplay, accepted: false, writeAttempted: false, preApplied: false, default);
                }
                else if (dropped.ApplyManualOnAccept)
                {
                    OnDeferredLiveManualSettled(link, accepted: false, preApplied: false, default);
                }
            }
        }
    }

    private async Task PumpAsync(Link link)
    {
        try
        {
            await foreach (var chunk in link.Connection.ReadOutputAsync(link.Cancel.Token).ConfigureAwait(false))
            {
                lock (_gate)
                {
                    if (_core is null || _link != link) return;
                    // An empty chunk of a mirror stands for one event, at this place between the chunks around it.
                    if (chunk.IsEmpty && link.Connection is ITerminalMirrorConnection mirror) ApplyMirrorEvent(mirror);
                    if (!chunk.IsEmpty && _core is not null)
                    {
                        _core.Write(chunk.Span);
                        _cachedInputModes = _core.ReadInputModes();
                        _history?.AppendOutput(_head, chunk.Span);
                        foreach (var tap in _taps) tap.OnOutput(_head, chunk.Span);
                        _head += (ulong)chunk.Length;
                    }
                }
                RequestFlush();
            }
        }
        catch (OperationCanceledException) { }
        catch { }

        TerminalExit exit;
        try { exit = await link.Connection.Exited.ConfigureAwait(false); }
        catch { exit = new TerminalExit(null, null); }
        // Known at once, also where nothing runs the UI thread's queue (a session with no view).
        link.Writes.Writer.TryComplete();
        // Release this process generation's lease immediately on exit without waiting for UI dispatch.
        link.ReleaseLaunch();
        lock (_gate)
        {
            if (_link == link)
            {
                _lastExit = exit;
                IsRunning = false;
            }
        }
        var hadRecovery = false;
        lock (link.WriteGate)
        {
            hadRecovery = link.HasUnresolvedCancelledPaste || link.HeldManualWrites.Count > 0;
            link.HasUnresolvedCancelledPaste = false;
            link.HeldReplayInFlight = false;
            link.HasUncertainHeldWrite = false;
            link.PendingLiveManualAccounting = 0;
            link.InFlightFirstTurnOrder = 0;
            link.InFlightTurnCount = 0;
            link.InFlightConsumedCount = 0;
            link.RecoveryId = 0;
            link.HeldManualWrites.Clear();
            link.DeferredManualWrites.Clear();
        }
        if (hadRecovery) PendingInputRecoveryChanged?.Invoke();
        Dispatcher.UIThread.Post(() =>
        {
            if (_link != link)
            {
                if (!link.StopRequested)
                {
                    LinkExited?.Invoke(link.Generation, link.LinkId, exit);
                }
                return;
            }
            IsRunning = false;
            Exited?.Invoke(exit);
            LinkExited?.Invoke(link.Generation, link.LinkId, exit);
        });
    }

    // One UI frame per burst of output, coalesced to ~60 Hz. While hidden or held by Mode 2026 only a dirty flag is kept.
    private void RequestFlush()
    {
        if (_disposed) return;
        if (!_visible)
        {
            _dirtyWhileHidden = true;
            return;
        }
        if (_renderHeld)
        {
            _dirtyDuringHold = true;
            return;
        }
        if (Interlocked.Exchange(ref _flushPending, 1) != 0) return;
        var elapsedMs = Environment.TickCount64 - Interlocked.Read(ref _lastFlushTick);
        if (elapsedMs >= (long)MinFrameInterval.TotalMilliseconds)
        {
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Render);
        }
        else
        {
            var delay = Math.Max(1, (long)MinFrameInterval.TotalMilliseconds - elapsedMs);
            lock (_gate)
            {
                if (_disposed) return;
                _coalesceTimer ??= new Timer(static s => ((TerminalSession)s!).OnCoalesceTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _coalesceTimer.Change(TimeSpan.FromMilliseconds(delay), Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnCoalesceTimer()
    {
        if (_disposed) return;
        if (!_visible)
        {
            _dirtyWhileHidden = true;
            Volatile.Write(ref _flushPending, 0);
            return;
        }
        Dispatcher.UIThread.Post(Flush, DispatcherPriority.Render);
    }

    private void Flush()
    {
        if (!_isVisible())
        {
            _visible = false;
            _dirtyWhileHidden = true;
            Volatile.Write(ref _flushPending, 0);
            return;
        }
        _visible = true;
        if (_renderHeld)
        {
            _dirtyDuringHold = true;
            Volatile.Write(ref _flushPending, 0);
            return;
        }
        FrameSnapshot? frame;
        lock (_gate)
        {
            Volatile.Write(ref _flushPending, 0);
            Interlocked.Exchange(ref _lastFlushTick, Environment.TickCount64);
            if (_core is null) return;
            _core.ActiveSearch?.Update(); // matches must be current before the frame takes its highlights
            frame = _core.CaptureFrame(force: _dirtyWhileHidden);
            _dirtyWhileHidden = false;
        }
        if (frame is not null) FrameReady?.Invoke(frame);
    }

    private void ReleaseRenderHold()
    {
        if (!_renderHeld) return;
        _renderHeld = false;
        if (_dirtyDuringHold)
        {
            _dirtyDuringHold = false;
            RequestFlush();
        }
    }

    // ---- ITerminalEffects: called inside Write/Resize with the lock held ----

    void ITerminalEffects.RenderHold(bool hold)
    {
        if (hold)
        {
            _renderHeld = true;
            _renderHoldTimer ??= new Timer(static s => ((TerminalSession)s!).ReleaseRenderHold(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _renderHoldTimer.Change(MaxRenderHold, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _renderHoldTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _renderHeld = false;
            if (_dirtyDuringHold)
            {
                _dirtyDuringHold = false;
                RequestFlush();
            }
        }
    }

    void ITerminalEffects.WriteToPty(ReadOnlySpan<byte> reply)
    {
        if (!IsMirror) TrySendProtocolReply(reply);
    }

    void ITerminalEffects.TitleChanged(string title)
    {
        if (_title == title) return;
        _title = title;
        Dispatcher.UIThread.Post(() => TitleChanged?.Invoke(title));
    }

    void ITerminalEffects.Bell() => Dispatcher.UIThread.Post(() => Bell?.Invoke());
}
