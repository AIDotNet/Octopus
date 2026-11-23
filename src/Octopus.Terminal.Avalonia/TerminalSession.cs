using System.Threading.Channels;
using Avalonia.Threading;
using Octopus.Core.Terminals;
using TKCode.Terminal.Core;

namespace Octopus.Terminal.Avalonia;

/// <summary>
/// One terminal: a <see cref="TerminalCore"/> fed by a connection's output. One lock serialises every access to the
/// core. Events are raised on the UI thread. Effect callbacks run inside <c>Write</c>/<c>Resize</c> under that lock,
/// so they only queue work and never touch the core again.
/// </summary>
public sealed class TerminalSession : ITerminalEffects, IAsyncDisposable
{
    private sealed class Link
    {
        public required ITerminalConnection Connection { get; init; }
        public Channel<byte[]> Writes { get; } = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
        public CancellationTokenSource Cancel { get; } = new();
    }

    private readonly object _gate = new();
    private readonly LaunchSpec _spec;
    private readonly ITerminalConnectionFactory _factory;
    private Func<bool> _isVisible;
    private readonly List<ITerminalTap> _taps = [];
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
    private Link? _link;
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
    {
        _spec = spec;
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

    /// <summary>True from <see cref="Start"/> until the process exits.</summary>
    public bool IsRunning { get; private set; }

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
    public IDisposable Tap(ITerminalTap tap, TerminalSnapshotFormat format, out TerminalTapStart start)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed || _core is null, this);
            var snapshot = format == TerminalSnapshotFormat.Native
                ? _core.EncodeSnapshot()
                : System.Text.Encoding.UTF8.GetBytes(_core.FormatVt());
            start = new TerminalTapStart(_head, format, snapshot, _size, Title);
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
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _core = TerminalCore.Create(Options(_size), this);
            _core.SetDefaultColors(_initialPalette, _initialScheme);
            _cachedInputModes = _core.ReadInputModes();
            _requested = _size;
        }
        try { StartLink(); }
        catch
        {
            lock (_gate) { _core?.Dispose(); _core = null; }
            throw;
        }
    }

    /// <summary>Ends the current process if any, clears the screen and starts the same command again.</summary>
    public async Task RestartAsync()
    {
        await StopLinkAsync().ConfigureAwait(true);
        lock (_gate)
        {
            if (_disposed || _core is null) return;
            _core.Reset();
            _core.SetDefaultColors(_initialPalette, _initialScheme);
            _cachedInputModes = _core.ReadInputModes();
            foreach (var tap in _taps) tap.OnReset(_head);
            _head++;
        }
        LastExit = null;
        StartLink();
        ForceFlush();
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
    public void Send(ReadOnlySpan<byte> bytes) => _link?.Writes.Writer.TryWrite(bytes.ToArray());

    /// <summary>Queues UTF-8 text for the process.</summary>
    public void Send(string text) => _link?.Writes.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(text));

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
                    _size = new TerminalSize(change.Columns, change.Rows, _size.PixelWidth, _size.PixelHeight);
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
    public async ValueTask DisposeAsync()
    {
        Timer? coalesce;
        Timer? hold;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            coalesce = _coalesceTimer;
            _coalesceTimer = null;
            hold = _renderHoldTimer;
            _renderHoldTimer = null;
        }
        coalesce?.Dispose();
        hold?.Dispose();
        await StopLinkAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _core?.Dispose();
            _core = null;
        }
    }

    private void StartLink()
    {
        var connection = _factory.Start(_spec, _size.Columns, _size.Rows);
        var link = new Link { Connection = connection };
        _link = link;
        IsRunning = true;
        _ = Task.Run(() => WriteLoopAsync(link));
        _ = Task.Run(() => PumpAsync(link));
        _ = connection.ResizeAsync(_size.Columns, _size.Rows, _size.PixelWidth, _size.PixelHeight).AsTask();
    }

    private async Task StopLinkAsync()
    {
        var link = _link;
        _link = null;
        IsRunning = false;
        if (link is null) return;
        link.Writes.Writer.TryComplete();
        try { await link.Connection.DisposeAsync().ConfigureAwait(false); } catch { }
        link.Cancel.Cancel();
        link.Cancel.Dispose();
    }

    private async Task WriteLoopAsync(Link link)
    {
        try
        {
            await foreach (var bytes in link.Writes.Reader.ReadAllAsync(link.Cancel.Token).ConfigureAwait(false))
                await link.Connection.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch { /* the process is gone; its exit is reported by the pump */ }
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
        lock (_gate)
        {
            if (_link == link) _lastExit = exit;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (_link != link) return; // replaced by a restart or closed
            IsRunning = false;
            Exited?.Invoke(exit);
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
        if (!IsMirror) Send(reply);
    }

    void ITerminalEffects.TitleChanged(string title)
    {
        if (_title == title) return;
        _title = title;
        Dispatcher.UIThread.Post(() => TitleChanged?.Invoke(title));
    }

    void ITerminalEffects.Bell() => Dispatcher.UIThread.Post(() => Bell?.Invoke());
}
