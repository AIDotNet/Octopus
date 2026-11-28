using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Octopus.Core.Terminals;
using Octopus.Terminal.Avalonia;
using TKCode.Terminal.Core;
using Xunit;

namespace Octopus.Terminal.Tests;

[Collection(TerminalSessionCollection.Name)]
public class SessionDisposalTests
{
    private sealed class DummyConnection : ITerminalConnection, ITerminalConnectionFactory
    {
        private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource<TerminalExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 1;
        public Task<TerminalExit> Exited => _exit.Task;

        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows) => this;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var chunk in _output.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight) => ValueTask.CompletedTask;
        public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;
        public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);

        public ValueTask DisposeAsync()
        {
            _output.Writer.TryComplete();
            _exit.TrySetResult(new TerminalExit(0, null));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DummyTap : ITerminalTap
    {
        public void OnOutput(ulong position, ReadOnlySpan<byte> bytes) { }
        public void OnResize(ulong position, TerminalSize size) { }
        public void OnReset(ulong position) { }
    }

    private sealed class DummySubscriber
    {
        public void OnFrame(FrameSnapshot _) { }
        public void OnTitle(string _) { }
        public void OnBell() { }
        public void OnExit(TerminalExit _) { }
        public void OnSizeOwner() { }
    }

    [Fact]
    public async Task DisposeAsync_clears_taps_size_owner_and_event_subscribers()
    {
        var wire = new DummyConnection();
        var session = new TerminalSession(
            new LaunchSpec("sh", [], null, new Dictionary<string, string>()),
            wire,
            new TerminalSize(80, 24, 8, 16),
            TerminalPalettes.Dark,
            ColorScheme.Dark,
            scrollbackLines: 1000,
            isVisible: static () => false);
        session.Start();

        var refs = AttachTrackedState(session);
        Assert.True(HasSizeOwner(session));

        await session.DisposeAsync();
        Assert.False(HasSizeOwner(session));

        for (var i = 0; i < 6 && refs.Any(static r => r.IsAlive); i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.All(refs, static r => Assert.False(r.IsAlive));
        GC.KeepAlive(session);
    }

    private sealed class DisposableCounter : IDisposable
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class StagedExitConnection : ITerminalConnection, ITerminalConnectionFactory
    {
        private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource _processExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<TerminalExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessId => 101;
        public Task ProcessExited => _processExited.Task;
        public Task<TerminalExit> Exited => _exited.Task;

        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows) => this;

        public void SignalProcessExited() => _processExited.TrySetResult();

        public void EmitOutput(string text) => _output.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(text));

        public void FinishOutputAndExit(int exitCode)
        {
            _output.Writer.TryComplete();
            _exited.TrySetResult(new TerminalExit(exitCode, null));
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var chunk in _output.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight) => ValueTask.CompletedTask;
        public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;
        public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);

        public ValueTask DisposeAsync()
        {
            _processExited.TrySetResult();
            _output.Writer.TryComplete();
            _exited.TrySetResult(new TerminalExit(0, null));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class GatedDisposeConnection(TaskCompletionSource? disposeEntered = null, TaskCompletionSource? allowDisposeFinish = null) : ITerminalConnection
    {
        private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource<TerminalExit> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public int ProcessId => 202;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public Task<TerminalExit> Exited => _exited.Task;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var chunk in _output.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight) => ValueTask.CompletedTask;
        public ValueTask SignalAsync(TerminalSignal signal) => ValueTask.CompletedTask;
        public ValueTask<string?> GetForegroundProcessNameAsync() => ValueTask.FromResult<string?>(null);

        public async ValueTask DisposeAsync()
        {
            disposeEntered?.TrySetResult();
            if (allowDisposeFinish is not null)
            {
                await allowDisposeFinish.Task.ConfigureAwait(false);
            }
            Interlocked.Exchange(ref _disposed, 1);
            _output.Writer.TryComplete();
            _exited.TrySetResult(new TerminalExit(0, null));
        }
    }

    private sealed class OverlappingRestartFactory(TaskCompletionSource firstDisposeEntered, TaskCompletionSource allowFirstDispose) : ITerminalConnectionFactory
    {
        private readonly object _gate = new();
        public List<GatedDisposeConnection> Connections { get; } = [];

        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows)
        {
            lock (_gate)
            {
                var conn = Connections.Count == 0
                    ? new GatedDisposeConnection(firstDisposeEntered, allowFirstDispose)
                    : new GatedDisposeConnection();
                Connections.Add(conn);
                return conn;
            }
        }
    }

    [Fact]
    public async Task Lease_disposes_on_native_process_exit_before_output_drain_while_preserving_output_and_exit_event()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = new StagedExitConnection();
        var lease = new DisposableCounter();

        await using var session = new TerminalSession(
            _ => new TerminalLaunch(new LaunchSpec("sh", [], null, new Dictionary<string, string>()), lease),
            connection,
            new TerminalSize(80, 24, 8, 16),
            TerminalPalettes.Dark,
            ColorScheme.Dark,
            scrollbackLines: 1000,
            isVisible: static () => false);
        session.Start();

        Assert.Equal(0, lease.DisposeCount);

        // Native child process exits while trailing PTY output has not drained yet.
        connection.SignalProcessExited();

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (lease.DisposeCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, ct);
        }

        Assert.Equal(1, lease.DisposeCount);
        Assert.False(connection.Exited.IsCompleted);
        Assert.True(session.IsRunning);

        // Trailing output emitted before EOF is still rendered, and drain-aware exit is preserved.
        connection.EmitOutput("final-trailing-output\r\n");
        connection.FinishOutputAndExit(7);

        deadline = DateTime.UtcNow.AddSeconds(3);
        while (session.LastExit is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, ct);
        }

        Assert.Equal(new TerminalExit(7, null), session.LastExit);
        Assert.Contains("final-trailing-output", session.ReadText(), StringComparison.Ordinal);
        Assert.Equal(1, lease.DisposeCount);
    }

    [Fact]
    public async Task Overlapping_restarts_and_concurrent_dispose_do_not_orphan_connections_or_leases()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1. Overlapping RestartAsync calls while first teardown is awaiting DisposeAsync.
        var firstDisposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new OverlappingRestartFactory(firstDisposeEntered, allowFirstDispose);
        var leases = new List<DisposableCounter>();
        var leaseGate = new object();

        var session = new TerminalSession(
            _ =>
            {
                var counter = new DisposableCounter();
                lock (leaseGate) leases.Add(counter);
                return new TerminalLaunch(new LaunchSpec("sh", [], null, new Dictionary<string, string>()), counter);
            },
            factory,
            new TerminalSize(80, 24, 8, 16),
            TerminalPalettes.Dark,
            ColorScheme.Dark,
            scrollbackLines: 1000,
            isVisible: static () => false);
        session.Start();

        var restart1 = session.RestartAsync();
        await firstDisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var restart2 = session.RestartAsync();
        allowFirstDispose.TrySetResult();
        await Task.WhenAll(restart1, restart2).WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.True(session.IsRunning);
        Assert.Single(factory.Connections, static c => !c.IsDisposed);
        lock (leaseGate)
        {
            Assert.Single(leases, static l => l.DisposeCount == 0);
            Assert.All(leases.Where(static l => l.DisposeCount > 0), static l => Assert.Equal(1, l.DisposeCount));
        }

        await session.DisposeAsync();
        Assert.All(factory.Connections, static c => Assert.True(c.IsDisposed));
        lock (leaseGate)
        {
            Assert.All(leases, static l => Assert.Equal(1, l.DisposeCount));
        }

        // 2. Concurrent RestartAsync + DisposeAsync while teardown is awaiting DisposeAsync.
        var disposeEntered2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDispose2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory2 = new OverlappingRestartFactory(disposeEntered2, allowDispose2);
        var leases2 = new List<DisposableCounter>();

        var session2 = new TerminalSession(
            _ =>
            {
                var counter = new DisposableCounter();
                lock (leaseGate) leases2.Add(counter);
                return new TerminalLaunch(new LaunchSpec("sh", [], null, new Dictionary<string, string>()), counter);
            },
            factory2,
            new TerminalSize(80, 24, 8, 16),
            TerminalPalettes.Dark,
            ColorScheme.Dark,
            scrollbackLines: 1000,
            isVisible: static () => false);
        session2.Start();

        var overlappingRestart = session2.RestartAsync();
        await disposeEntered2.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var concurrentDispose = session2.DisposeAsync().AsTask();
        allowDispose2.TrySetResult();
        await Task.WhenAll(overlappingRestart, concurrentDispose).WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.False(session2.IsRunning);
        Assert.All(factory2.Connections, static c => Assert.True(c.IsDisposed));
        lock (leaseGate)
        {
            Assert.All(leases2, static l => Assert.Equal(1, l.DisposeCount));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HasSizeOwner(TerminalSession session) => session.SizeOwner is not null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] AttachTrackedState(TerminalSession session)
    {
        var tap = new DummyTap();
        _ = session.Tap(tap, TerminalSnapshotFormat.Native, out _);

        var owner = new object();
        session.ClaimSize(owner, new TerminalSize(80, 24, 8, 16));

        var subscriber = new DummySubscriber();
        session.FrameReady += subscriber.OnFrame;
        session.TitleChanged += subscriber.OnTitle;
        session.Bell += subscriber.OnBell;
        session.Exited += subscriber.OnExit;
        session.SizeOwnerChanged += subscriber.OnSizeOwner;

        return [new WeakReference(tap), new WeakReference(owner), new WeakReference(subscriber)];
    }
}
