using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Octopus.Acp.Transport;

namespace Octopus.Acp.Client;

public sealed record AcpTerminalExitStatus(int? ExitCode, string? Signal);

public sealed record AcpTerminalOutputResult(string Output, bool Truncated, AcpTerminalExitStatus? ExitStatus);

public sealed record AcpTerminalSnapshot(
    string TerminalId,
    string Command,
    string? WorkingDirectory,
    string Output,
    bool Truncated,
    AcpTerminalExitStatus? ExitStatus);

/// <summary>
/// Client-hosted terminal callbacks for ACP <c>terminal/create</c>, <c>terminal/output</c>,
/// <c>terminal/wait_for_exit</c>, <c>terminal/kill</c>, and <c>terminal/release</c>.
/// Advertised only when <c>clientCapabilities.terminal</c> is enabled by the user.
/// </summary>
public interface IAcpTerminalHandler : IAsyncDisposable
{
    Task<string> CreateAsync(
        string sessionId,
        string command,
        IReadOnlyList<string>? args = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? cwd = null,
        int? outputByteLimit = null,
        CancellationToken cancellationToken = default);

    Task<AcpTerminalOutputResult> GetOutputAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default);

    Task<AcpTerminalExitStatus> WaitForExitAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default);

    Task KillAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default);

    Task ReleaseAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a live snapshot of a client-hosted terminal for GUI tool cards referencing <c>type: "terminal"</c>.</summary>
    AcpTerminalSnapshot? TryGetSnapshot(string terminalId);
}

/// <summary>
/// Default local subprocess terminal host implementing the full ACP v1 <c>terminal/*</c> lifecycle with
/// UTF-8-boundary-safe byte-limited output truncation and clean process-tree teardown.
/// </summary>
public sealed class AcpTerminalHandler : IAcpTerminalHandler
{
    public const int DefaultOutputByteLimit = 1024 * 1024;
    private const int MinOutputByteLimit = 1024;

    private readonly string _defaultWorkingDirectory;
    private readonly IReadOnlyDictionary<string, string>? _baseEnvironment;
    private readonly int _defaultOutputByteLimit;
    private readonly ConcurrentDictionary<string, ManagedTerminal> _terminals = new(StringComparer.Ordinal);
    private long _nextId;
    private int _disposed;

    public AcpTerminalHandler(
        string defaultWorkingDirectory,
        IReadOnlyDictionary<string, string>? baseEnvironment = null,
        int defaultOutputByteLimit = DefaultOutputByteLimit)
    {
        _defaultWorkingDirectory = defaultWorkingDirectory;
        _baseEnvironment = baseEnvironment;
        _defaultOutputByteLimit = Math.Max(MinOutputByteLimit, defaultOutputByteLimit);
    }

    public Task<string> CreateAsync(
        string sessionId,
        string command,
        IReadOnlyList<string>? args = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? cwd = null,
        int? outputByteLimit = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new AcpRpcException(-32602, "ACP terminal/create requires a non-empty command.");
        }

        var workingDir = !string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd)
            ? cwd
            : Directory.Exists(_defaultWorkingDirectory) ? _defaultWorkingDirectory : Environment.CurrentDirectory;

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var useShell = args is null && NeedsShellExecution(command);
        if (useShell)
        {
            if (OperatingSystem.IsWindows())
            {
                psi.FileName = "cmd.exe";
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add(command);
            }
            else
            {
                psi.FileName = "/bin/sh";
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(command);
            }
        }
        else
        {
            psi.FileName = command;
            if (args is not null)
            {
                foreach (var a in args) psi.ArgumentList.Add(a);
            }
        }

        if (_baseEnvironment is not null)
        {
            psi.Environment.Clear();
            foreach (var (k, v) in _baseEnvironment)
            {
                psi.Environment[k] = v;
            }
        }
        if (env is not null)
        {
            foreach (var (k, v) in env)
            {
                if (!string.IsNullOrWhiteSpace(k))
                {
                    psi.Environment[k] = v;
                }
            }
        }

        var proc = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            if (!proc.Start())
            {
                proc.Dispose();
                throw new AcpRpcException(-32603, $"Failed to start terminal command '{command}'.");
            }
        }
        catch (Exception ex) when (ex is not AcpRpcException)
        {
            proc.Dispose();
            throw new AcpRpcException(-32603, $"Failed to start terminal command '{command}': {ex.Message}");
        }

        try { proc.StandardInput.Close(); } catch { }

        var id = $"term_{Interlocked.Increment(ref _nextId)}";
        var limit = outputByteLimit.HasValue ? Math.Max(MinOutputByteLimit, outputByteLimit.Value) : _defaultOutputByteLimit;
        var displayCommand = args is { Count: > 0 } ? $"{command} {string.Join(' ', args)}" : command;
        var managed = new ManagedTerminal(id, displayCommand, workingDir, proc, limit);
        _terminals[id] = managed;
        return Task.FromResult(id);
    }

    public Task<AcpTerminalOutputResult> GetOutputAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default)
    {
        var term = RequireTerminal(terminalId);
        return Task.FromResult(term.ReadOutput());
    }

    public Task<AcpTerminalExitStatus> WaitForExitAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default)
    {
        var term = RequireTerminal(terminalId);
        return term.WaitForExitAsync(cancellationToken);
    }

    public Task KillAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default)
    {
        var term = RequireTerminal(terminalId);
        term.Kill();
        return Task.CompletedTask;
    }

    public async Task ReleaseAsync(
        string sessionId,
        string terminalId,
        CancellationToken cancellationToken = default)
    {
        if (!_terminals.TryRemove(terminalId, out var term))
        {
            throw new AcpRpcException(-32002, $"Unknown terminalId: {terminalId}");
        }
        await term.DisposeAsync().ConfigureAwait(false);
    }

    public AcpTerminalSnapshot? TryGetSnapshot(string terminalId)
    {
        if (!_terminals.TryGetValue(terminalId, out var term))
        {
            return null;
        }
        var output = term.ReadOutput();
        return new AcpTerminalSnapshot(
            term.Id,
            term.Command,
            term.WorkingDirectory,
            output.Output,
            output.Truncated,
            output.ExitStatus);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var (id, _) in _terminals)
        {
            if (_terminals.TryRemove(id, out var term))
            {
                await term.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private ManagedTerminal RequireTerminal(string terminalId)
    {
        if (string.IsNullOrWhiteSpace(terminalId) || !_terminals.TryGetValue(terminalId, out var term))
        {
            throw new AcpRpcException(-32002, $"Unknown terminalId: {terminalId}");
        }
        return term;
    }

    private static bool NeedsShellExecution(string command)
    {
        if (File.Exists(command)) return false;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (char.IsWhiteSpace(c) || c is '|' or '&' or ';' or '>' or '<' or '$' or '`' or '"' or '\'')
            {
                return true;
            }
        }
        return false;
    }

    private sealed class ManagedTerminal : IAsyncDisposable
    {
        private readonly System.Diagnostics.Process _process;
        private readonly int _outputByteLimit;
        private readonly object _gate = new();
        private readonly List<byte> _buffer = new(4096);
        private readonly TaskCompletionSource<AcpTerminalExitStatus> _exitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _monitorTask;
        private bool _truncated;
        private bool _killed;
        private AcpTerminalExitStatus? _exitStatus;
        private int _disposed;

        public ManagedTerminal(string id, string command, string workingDirectory, System.Diagnostics.Process process, int outputByteLimit)
        {
            Id = id;
            Command = command;
            WorkingDirectory = workingDirectory;
            _process = process;
            _outputByteLimit = outputByteLimit;
            _monitorTask = Task.Run(MonitorAsync);
        }

        public string Id { get; }
        public string Command { get; }
        public string WorkingDirectory { get; }

        public AcpTerminalOutputResult ReadOutput()
        {
            lock (_gate)
            {
                var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_buffer);
                var text = span.IsEmpty ? "" : Encoding.UTF8.GetString(span);
                return new AcpTerminalOutputResult(text, _truncated, _exitStatus);
            }
        }

        public Task<AcpTerminalExitStatus> WaitForExitAsync(CancellationToken cancellationToken) =>
            _exitTcs.Task.WaitAsync(cancellationToken);

        public void Kill()
        {
            _killed = true;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch { /* Already exited or race. */ }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Kill();
            try { await _monitorTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            try { _process.Dispose(); } catch { }
        }

        private async Task MonitorAsync()
        {
            var stdoutTask = PumpStreamAsync(_process.StandardOutput.BaseStream);
            var stderrTask = PumpStreamAsync(_process.StandardError.BaseStream);
            try
            {
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch { }
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch { }

            int? exitCode = null;
            try { exitCode = _process.ExitCode; } catch { }
            var status = _killed && !OperatingSystem.IsWindows()
                ? new AcpTerminalExitStatus(exitCode, "SIGKILL")
                : new AcpTerminalExitStatus(exitCode, null);

            lock (_gate)
            {
                _exitStatus = status;
            }
            _exitTcs.TrySetResult(status);
        }

        private async Task PumpStreamAsync(Stream stream)
        {
            var chunk = new byte[8192];
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
                    if (read == 0) break;
                    AppendBytes(chunk.AsSpan(0, read));
                }
            }
            catch { /* Stream closed on exit/dispose. */ }
        }

        private void AppendBytes(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return;
            lock (_gate)
            {
                if (data.Length >= _outputByteLimit)
                {
                    _buffer.Clear();
                    var start = data.Length - _outputByteLimit;
                    // Advance past any UTF-8 continuation bytes so truncation preserves valid characters.
                    while (start < data.Length && (data[start] & 0xC0) == 0x80) start++;
                    for (var i = start; i < data.Length; i++) _buffer.Add(data[i]);
                    _truncated = true;
                    return;
                }

                var overflow = (_buffer.Count + data.Length) - _outputByteLimit;
                if (overflow > 0)
                {
                    var removeCount = Math.Min(overflow, _buffer.Count);
                    while (removeCount < _buffer.Count && (_buffer[removeCount] & 0xC0) == 0x80)
                    {
                        removeCount++;
                    }
                    _buffer.RemoveRange(0, removeCount);
                    _truncated = true;
                }

                for (var i = 0; i < data.Length; i++) _buffer.Add(data[i]);
            }
        }
    }
}
