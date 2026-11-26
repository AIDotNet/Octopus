using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Octopus.Core.Terminals;
using Octopus.Remote.Protocol;
using Octopus.Terminal;

namespace Octopus.Remote.Client;

/// <summary>
/// A terminal on a host, as a connection: its output comes from the host, what is typed goes there. The terminal that
/// shows it is a mirror (<see cref="ITerminalMirrorConnection"/>): the host's terminal answers the program's queries
/// and has the say over the size, this one follows.
/// </summary>
internal sealed class RemoteTerminalConnection : ITerminalMirrorConnection
{
    /// <summary>The host is told how far this side got after this many bytes (design/12 §6.4).</summary>
    private const int AckEvery = 32 * 1024;

    private readonly RemoteHostClient _client;
    private readonly string _itemId;
    private readonly bool _restart;
    private readonly Channel<Incoming> _incoming = Channel.CreateUnbounded<Incoming>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Queue<TerminalMirrorEvent> _events = new();
    private readonly TaskCompletionSource<TerminalExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _closed = new();
    private uint _channel;
    private int _columns, _rows;
    private volatile bool _ownsSize;
    // Who decides the size is told by the answers to this view's own calls and by the resize records in the output, on
    // different threads. Under this gate they are applied one at a time and an answer older than one applied is dropped,
    // so a stale "not yours" cannot land after the claim's "yours" (it did, on a slow machine).
    private readonly Lock _sizeGate = new();
    private long _sizeCalls;
    private long _sizeApplied;
    private int _disposed;

    private enum IncomingKind { Frame, Exited, Resync, Lost }

    /// <summary>What arrived for this terminal, in the order it arrived: a data frame, or one of the three ways the output ends or restarts.</summary>
    private readonly record struct Incoming(IncomingKind Kind, FrameType Type = default, byte[]? Payload = null, int? ExitCode = null);

    private readonly bool _claimOnAttach;
    private bool _hasScreen;
    private ulong _lastPosition;

    /// <param name="claimOnAttach">False: only look, at whatever size the terminal has; the size is taken by typing or by <see cref="ClaimSizeAsync"/>.</param>
    public RemoteTerminalConnection(RemoteHostClient client, string itemId, int columns, int rows, bool restart, bool claimOnAttach = true)
    {
        _client = client;
        _itemId = itemId;
        _restart = restart;
        _claimOnAttach = claimOnAttach;
        (_columns, _rows) = (columns, rows);
        _channel = client.Register(this);
    }

    public int ProcessId => 0;
    public Task<TerminalExit> Exited => _exit.Task;
    /// <summary>True when the output ended because the program on the host ended (not because the connection did).</summary>
    public bool ProgramExited { get; private set; }
    /// <summary>True while this view decides the terminal's size.</summary>
    public bool OwnsSize => _ownsSize;
    /// <summary>The terminal's size on the host changed, or who decides it: columns, rows, and whether it is this view. Raised on a background thread.</summary>
    public event Action<int, int, bool>? SizeChanged;

    /// <summary>Makes this view the one that decides the size, with this size.</summary>
    public Task ClaimSizeAsync(int columns, int rows)
    {
        (_columns, _rows) = (columns, rows);
        return ResizeAsync(columns, rows, claim: true);
    }

    /// <summary>Gives the say over the size back to the view that had it before, and keeps looking.</summary>
    public async Task ReleaseSizeAsync()
    {
        if (_disposed != 0) return;
        try
        {
            var call = Interlocked.Increment(ref _sizeCalls);
            var released = await _client.CallAsync(RemoteMethods.SessionRelease, new ChannelMessage { Channel = _channel },
                RemoteJsonContext.Default.ChannelMessage, RemoteJsonContext.Default.ResizedMessage, _closed.Token).ConfigureAwait(false);
            ApplySize(call, released.Columns, released.Rows, owns: false);
        }
        // An older host does not know how to let go: the size stays this view's until it leaves.
        catch (Exception e) when (e is IOException or RemoteException or OperationCanceledException) { }
    }

    // ---- from the client's receiving thread: only queue ----

    public void OnFrame(FrameType type, ReadOnlyMemory<byte> payload) => _incoming.Writer.TryWrite(new Incoming(IncomingKind.Frame, type, payload.ToArray()));
    public void OnExited(int? exitCode) => _incoming.Writer.TryWrite(new Incoming(IncomingKind.Exited, ExitCode: exitCode));
    public void OnResync() => _incoming.Writer.TryWrite(new Incoming(IncomingKind.Resync));
    public void OnConnectionLost() => _incoming.Writer.TryWrite(new Incoming(IncomingKind.Lost));

    public TerminalMirrorEvent? TakeEvent()
    {
        lock (_events) return _events.Count > 0 ? _events.Dequeue() : null;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        var token = linked.Token;
        TerminalExit exit = new(null, null);
        string? failure = null;
        try
        {
            if (_restart)
            {
                await _client.CallAsync(RemoteMethods.SessionRestart, new ItemRefMessage { ItemId = _itemId },
                    RemoteJsonContext.Default.ItemRefMessage, RemoteJsonContext.Default.Nothing, token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is RemoteException or IOException) { failure = e.Message; }

        AttachedMessage? attached = null;
        if (failure is null)
        {
            try { attached = await AttachAsync(claim: _claimOnAttach, token).ConfigureAwait(false); }
            catch (Exception e) when (e is RemoteException or IOException) { failure = e.Message; }
        }
        if (attached is null)
        {
            // Nothing to show but why: the terminal says it, then reports the end.
            yield return System.Text.Encoding.UTF8.GetBytes("\u001b[31m" + failure + "\u001b[0m\r\n");
            _exit.TrySetResult(exit);
            yield break;
        }

        yield return Announce(new TerminalMirrorEvent(TerminalMirrorEventKind.Resize, attached.Columns, attached.Rows));
        var snapshot = new MemoryStream();
        var native = attached.Snapshot == SnapshotFormats.Native;
        ulong processed = attached.Position, acknowledged = attached.Position;
        _lastPosition = processed;
        if (attached.Resumed) _hasScreen = true;
        if (!attached.IsRunning && attached.ExitCode is { } already) exit = new TerminalExit(already, null);

        await foreach (var item in ReadIncomingAsync(token).ConfigureAwait(false))
        {
            if (item.Kind == IncomingKind.Lost) break;
            if (item.Kind == IncomingKind.Resync)
            {
                // The host stopped feeding this terminal because it fell behind: start over from a fresh snapshot (or ring delta if still within window).
                _client.Unregister(_channel);
                _channel = _client.Register(this);
                AttachedMessage? again = null;
                try { again = await AttachAsync(claim: _ownsSize, token).ConfigureAwait(false); }
                catch (Exception e) when (e is RemoteException or IOException) { }
                if (again is null) break;
                native = again.Snapshot == SnapshotFormats.Native;
                snapshot = new MemoryStream();
                processed = acknowledged = again.Position;
                _lastPosition = processed;
                if (!again.Resumed && !native) yield return Announce(new TerminalMirrorEvent(TerminalMirrorEventKind.Reset));
                yield return Announce(new TerminalMirrorEvent(TerminalMirrorEventKind.Resize, again.Columns, again.Rows));
                continue;
            }
            if (item.Kind == IncomingKind.Exited)
            {
                ProgramExited = true;
                exit = new TerminalExit(item.ExitCode, null);
                break;
            }
            var payload = item.Payload ?? [];
            switch (item.Type)
            {
                case FrameType.Snapshot when payload.Length >= 1:
                    snapshot.Write(payload, 1, payload.Length - 1);
                    if ((payload[0] & 1) == 0) break;
                    var whole = snapshot.ToArray();
                    snapshot = new MemoryStream();
                    _hasScreen = true;
                    _lastPosition = processed;
                    if (native) yield return Announce(new TerminalMirrorEvent(TerminalMirrorEventKind.Snapshot, Snapshot: whole));
                    else if (whole.Length > 0) yield return whole;
                    break;
                case FrameType.Output when payload.Length >= 8:
                    var bytes = payload.AsMemory(8);
                    processed = BinaryPrimitives.ReadUInt64LittleEndian(payload) + (ulong)bytes.Length;
                    _hasScreen = true;
                    _lastPosition = processed;
                    if (!bytes.IsEmpty) yield return bytes;
                    if (processed - acknowledged >= AckEvery)
                    {
                        acknowledged = processed;
                        await AcknowledgeAsync(processed).ConfigureAwait(false);
                    }
                    break;
                case FrameType.Record when payload.Length >= 9:
                    processed = BinaryPrimitives.ReadUInt64LittleEndian(payload) + 1;
                    _hasScreen = true;
                    _lastPosition = processed;
                    if (payload[8] == RecordKinds.Resize && payload.Length >= 13)
                    {
                        lock (_sizeGate) SizeChanged?.Invoke(BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(9)), BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(11)), _ownsSize);
                        yield return Announce(new TerminalMirrorEvent(TerminalMirrorEventKind.Resize,
                            BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(9)), BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(11))));
                    }
                    else if (payload[8] == RecordKinds.Reset)
                    {
                        yield return Announce(new TerminalMirrorEvent(TerminalMirrorEventKind.Reset));
                    }
                    break;
            }
        }
        _exit.TrySetResult(exit);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        if (_disposed != 0 || input.IsEmpty) return;
        try
        {
            // Typing is using the terminal: its size becomes this view's again if another view had taken it.
            if (!_ownsSize) await ResizeAsync(_columns, _rows, claim: true).ConfigureAwait(false);
            await _client.SendDataAsync(FrameType.Input, _channel, input.Span).ConfigureAwait(false);
        }
        catch (IOException) { }
    }

    public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight)
    {
        (_columns, _rows) = (columns, rows);
        return new ValueTask(ResizeAsync(columns, rows, claim: false));
    }

    public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;

    public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);

    /// <summary>Stops showing the terminal. The program on the host keeps running.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _closed.Cancel();
        _incoming.Writer.TryComplete();
        _client.Unregister(_channel);
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _client.CallAsync(RemoteMethods.SessionDetach, new ChannelMessage { Channel = _channel },
                RemoteJsonContext.Default.ChannelMessage, RemoteJsonContext.Default.Nothing, limit.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or RemoteException or OperationCanceledException) { }
        _exit.TrySetResult(new TerminalExit(null, null));
    }

    private async Task<AttachedMessage> AttachAsync(bool claim, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _sizeCalls);
        var resumeFrom = _hasScreen && _client.SupportsSessionResume ? (ulong?)_lastPosition : null;
        var attached = await _client.CallAsync(RemoteMethods.SessionAttach, new AttachMessage
        {
            ItemId = _itemId,
            Channel = _channel,
            Columns = _columns,
            Rows = _rows,
            Snapshot = _client.NativeSnapshots ? SnapshotFormats.Native : SnapshotFormats.Vt,
            ClaimSize = claim,
            ResumeFrom = resumeFrom,
        }, RemoteJsonContext.Default.AttachMessage, RemoteJsonContext.Default.AttachedMessage, cancellationToken).ConfigureAwait(false);
        ApplySize(call, attached.Columns, attached.Rows, attached.OwnsSize);
        return attached;
    }

    private async Task ResizeAsync(int columns, int rows, bool claim)
    {
        if (_disposed != 0) return;
        try
        {
            var call = Interlocked.Increment(ref _sizeCalls);
            var resized = await _client.CallAsync(RemoteMethods.SessionResize, new ResizeMessage { Channel = _channel, Columns = columns, Rows = rows, Claim = claim },
                RemoteJsonContext.Default.ResizeMessage, RemoteJsonContext.Default.ResizedMessage, _closed.Token).ConfigureAwait(false);
            ApplySize(call, resized.Columns, resized.Rows, resized.Applied);
        }
        catch (Exception e) when (e is IOException or RemoteException or OperationCanceledException) { }
    }

    /// <summary>The answer to size call number <paramref name="call"/>, unless a later call's answer is already applied.</summary>
    private void ApplySize(long call, int columns, int rows, bool owns)
    {
        lock (_sizeGate)
        {
            if (call < _sizeApplied) return;
            _sizeApplied = call;
            _ownsSize = owns;
            SizeChanged?.Invoke(columns, rows, owns);
        }
    }

    private async Task AcknowledgeAsync(ulong position)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, position);
        try { await _client.SendDataAsync(FrameType.Ack, _channel, payload).ConfigureAwait(false); }
        catch (IOException) { }
    }

    // An event takes effect at this place in the output: queued, and stood for by one empty chunk.
    private ReadOnlyMemory<byte> Announce(TerminalMirrorEvent change)
    {
        lock (_events) _events.Enqueue(change);
        return ReadOnlyMemory<byte>.Empty;
    }

    private async IAsyncEnumerable<Incoming> ReadIncomingAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            Incoming item;
            try
            {
                if (!await _incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) yield break;
                if (!_incoming.Reader.TryRead(out item)) continue;
            }
            catch (OperationCanceledException) { yield break; }
            yield return item;
        }
    }
}
