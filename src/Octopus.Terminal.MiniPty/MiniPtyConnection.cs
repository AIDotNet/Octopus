using System.Runtime.CompilerServices;
using MiniPty;
using Octopus.Core.Terminals;

namespace Octopus.Terminal.MiniPty;

/// <summary>An <see cref="ITerminalConnection"/> over a MiniPty session.</summary>
public sealed class MiniPtyConnection : ITerminalConnection
{
    private readonly PtySession _session;
    private readonly TaskCompletionSource _outputDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task<TerminalExit> _exited;
    private int _disposed;

    internal MiniPtyConnection(PtySession session)
    {
        _session = session;
        _exited = WaitForExitAsync();
    }

    /// <inheritdoc />
    public int ProcessId => _session.ProcessId;

    /// <inheritdoc />
    public Task<TerminalExit> Exited => _exited;

    /// <inheritdoc />
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var chunk in _session.ReadOutputAsync(cancellationToken).ConfigureAwait(false))
                yield return chunk.Data;
        }
        finally
        {
            _outputDone.TrySetResult();
        }
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default)
    {
        if (_session.HasExited) return ValueTask.CompletedTask;
        try { return new ValueTask(_session.WriteInputAsync(input, cancellationToken).AsTask()); }
        catch (ObjectDisposedException) { return ValueTask.CompletedTask; }
    }

    /// <inheritdoc />
    public ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight)
    {
        try
        {
            _session.Resize(new PtySize(columns, rows), new PtyPixelSize(Math.Max(0, pixelWidth), Math.Max(0, pixelHeight)));
        }
        catch (ObjectDisposedException) { }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask SignalAsync(TerminalSignal signal)
    {
        try
        {
            _session.Kill(signal switch
            {
                TerminalSignal.Interrupt => PtySignal.Interrupt,
                TerminalSignal.Hangup => PtySignal.Hangup,
                TerminalSignal.Terminate => PtySignal.Terminate,
                _ => PtySignal.Kill,
            });
        }
        catch (ObjectDisposedException) { }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<string?> GetForegroundProcessNameAsync()
    {
        try { return ValueTask.FromResult(_session.ActiveProcessName); }
        catch (ObjectDisposedException) { return ValueTask.FromResult<string?>(null); }
    }

    /// <summary>Hangup, wait up to two seconds, then kill. Always releases the pty.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (!_session.HasExited)
            {
                try { _session.Kill(PtySignal.Hangup); } catch (ObjectDisposedException) { }
                await Task.WhenAny(_exited, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
                if (!_session.HasExited)
                {
                    try { _session.Kill(PtySignal.Kill); } catch (ObjectDisposedException) { }
                }
            }
        }
        finally
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _outputDone.TrySetResult();
        }
    }

    private async Task<TerminalExit> WaitForExitAsync()
    {
        PtyExitStatus status;
        try { status = await _session.WaitForExitAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { return new TerminalExit(null, null); }
        // Output must be read to the end first, so the last bytes are on screen when the exit is announced.
        await _outputDone.Task.ConfigureAwait(false);
        return status.Signal is { } signal
            ? new TerminalExit(null, signal)
            : new TerminalExit(status.ExitCode, null);
    }
}
