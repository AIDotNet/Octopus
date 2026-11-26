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
