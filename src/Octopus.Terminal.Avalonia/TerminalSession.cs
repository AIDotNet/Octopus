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
    private readonly Func<bool> _isVisible;
    private readonly int _scrollbackLines;
    private readonly ColorScheme _initialScheme;
    private readonly TerminalPalette _initialPalette;
    private TerminalCore? _core;
    private TerminalSize _size;
    private Link? _link;
    private int _flushPending;
    private bool _dirtyWhileHidden;
    private bool _disposed;

    /// <param name="spec">What to run.</param>
    /// <param name="factory">Starts the pty.</param>
    /// <param name="size">Initial grid.</param>
    /// <param name="palette">Initial default colours.</param>
    /// <param name="scheme">Light or dark.</param>
    /// <param name="scrollbackLines">Scrollback limit in lines (also capped at 16 MB).</param>
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

    /// <summary>Creates the core and starts the process. Throws when the process cannot start.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _core = TerminalCore.Create(new TerminalCoreOptions(_size)
            {
                ScrollbackMaxLines = _scrollbackLines,
                ScrollbackMaxBytes = 16L * 1024 * 1024,
                Replies = new TerminalReplyProfile("Octopus", AdvertiseOsc52: false, _initialScheme),
            }, this);
            _core.SetDefaultColors(_initialPalette, _initialScheme);
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
        }
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

    /// <summary>Changes the grid: the core first, then the pty (with pixel size).</summary>
    public void Resize(TerminalSize size)
    {
        if (size.Columns < 1 || size.Rows < 1) return;
        Link? link;
        lock (_gate)
        {
            if (_core is null || size == _size) return;
            _size = size;
            _core.Resize(size);
            link = _link;
        }
        if (link is not null)
            _ = link.Connection.ResizeAsync(size.Columns, size.Rows, size.PixelWidth, size.PixelHeight).AsTask();
        RequestFlush();
    }

    /// <summary>Changes the default colours. Programs that ask for them get the new ones.</summary>
    public void SetPalette(TerminalPalette palette, ColorScheme scheme)
    {
        lock (_gate) _core?.SetDefaultColors(palette, scheme);
        ForceFlush();
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
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
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
                    _core.Write(chunk.Span);
                }
                RequestFlush();
            }
        }
        catch (OperationCanceledException) { }
        catch { }

        TerminalExit exit;
        try { exit = await link.Connection.Exited.ConfigureAwait(false); }
        catch { exit = new TerminalExit(null, null); }
        Dispatcher.UIThread.Post(() =>
        {
            if (_link != link) return; // replaced by a restart or closed
            IsRunning = false;
            Exited?.Invoke(exit);
        });
    }

    // One UI frame per burst of output. While hidden only a dirty flag is kept.
    private void RequestFlush()
    {
        if (Interlocked.Exchange(ref _flushPending, 1) == 0)
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Render);
    }

    private void Flush()
    {
        Volatile.Write(ref _flushPending, 0);
        if (!_isVisible())
        {
            _dirtyWhileHidden = true;
            return;
        }
        FrameSnapshot? frame;
        lock (_gate)
        {
            if (_core is null) return;
            _core.ActiveSearch?.Update(); // matches must be current before the frame takes its highlights
            frame = _core.CaptureFrame(force: _dirtyWhileHidden);
            _dirtyWhileHidden = false;
        }
        if (frame is not null) FrameReady?.Invoke(frame);
    }

    // ---- ITerminalEffects: called inside Write/Resize with the lock held ----

    void ITerminalEffects.WriteToPty(ReadOnlySpan<byte> reply) => Send(reply);

    void ITerminalEffects.TitleChanged(string title) => Dispatcher.UIThread.Post(() => TitleChanged?.Invoke(title));

    void ITerminalEffects.Bell() => Dispatcher.UIThread.Post(() => Bell?.Invoke());
}
