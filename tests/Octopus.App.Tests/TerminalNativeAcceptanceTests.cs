using System.Runtime.CompilerServices;
using Avalonia.Headless.XUnit;
using Octopus.App.Collab.Sessions;
using Octopus.Core.Terminals;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using Octopus.Terminal.MiniPty;
using TKCode.Terminal.Core;
using Xunit;

namespace Octopus.App.Tests;

public sealed class TerminalNativeAcceptanceTests
{
    private sealed class BufferedOutputConnection(ITerminalConnection inner, Task readGate) : ITerminalConnection
    {
        public int ProcessId => inner.ProcessId;
        public Task<TerminalExit> Exited => inner.Exited;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync([EnumeratorCancellation] CancellationToken token = default)
        {
            await readGate.WaitAsync(token);
            await foreach (var item in inner.ReadOutputAsync(token))
            {
                yield return item;
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken ct = default) => inner.WriteAsync(input, ct);
        public ValueTask ResizeAsync(int c, int r, int w, int h) => inner.ResizeAsync(c, r, w, h);
        public ValueTask SignalAsync(TerminalSignal signal) => inner.SignalAsync(signal);
        public ValueTask<string?> GetForegroundProcessNameAsync() => inner.GetForegroundProcessNameAsync();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class SingleConnectionFactory(ITerminalConnection connection) : ITerminalConnectionFactory
    {
        public ITerminalConnection Start(LaunchSpec spec, int columns, int rows) => connection;
    }

    [AvaloniaFact, FreshApp]
    public async Task Exited_minipty_with_buffered_output_must_refuse_prompt()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;

        var ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var spec = new LaunchSpec("/usr/bin/true", [], null, new Dictionary<string, string>());
        var inner = new MiniPtyConnectionFactory().Start(spec, 80, 24);
        var native = (global::MiniPty.PtySession)inner.GetType()
            .GetField("_session", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(inner)!;
        var registry = new TerminalSessionRegistry();
        var session = registry.GetOrStart(
            "exited",
            spec,
            new SingleConnectionFactory(new BufferedOutputConnection(inner, release.Task)),
            new TerminalSize(80, 24, 8, 16),
            TerminalPalettes.Dark,
            ColorScheme.Dark,
            1000,
            out _);
        using var bridge = new TerminalCollabSession("exited", "mock", "dummy", "/tmp", registry, null);
        try
        {
            await native.WaitForExitAsync(ct);
            Assert.True(native.HasExited);
            Assert.False(inner.Exited.IsCompleted);
            Assert.True(session.IsRunning);
            Assert.False(await bridge.DeliverCollabPromptAsync("impossible", "thread", "job", true, ct));
            Assert.Null(bridge.TakeAnswer(3500));
        }
        finally
        {
            release.TrySetResult();
            await registry.CloseAllAsync();
        }
    }
}
