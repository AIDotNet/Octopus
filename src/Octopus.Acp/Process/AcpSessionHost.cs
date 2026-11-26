using System.Diagnostics;
using System.Text;
using Octopus.Acp.Client;
using Octopus.Acp.Dialects;
using Octopus.Acp.Protocol;
using Octopus.Acp.Transport;
using Octopus.Core.Agents;

namespace Octopus.Acp.Process;

/// <summary>Live ACP session abstraction consumed by the GUI tab (mockable in headless tests).</summary>
public interface IAcpSession : IAsyncDisposable
{
    event Action<AcpRuntimeEvent>? EventReceived;
    bool IsConnected { get; }
    string? SessionId { get; }
    AcpInitializeResult? Initialized { get; }
    AcpSessionModeState? Modes { get; }
    IReadOnlyList<AcpSessionConfigOption> ConfigOptions { get; }
    AcpSessionModelState? Models { get; }
    IReadOnlyList<AcpAvailableCommand> AvailableCommands { get; }
    IAcpDialect Dialect { get; }
    string SourceLabel { get; }
    string GetStderrSnapshot();

    Task<AcpSessionStartResult> StartAsync(
        string workingDirectory,
        IReadOnlyList<string>? additionalDirectories = null,
        string? resumeSessionId = null,
        CancellationToken cancellationToken = default);
    Task<AcpPromptResult> PromptAsync(
        string text,
        IReadOnlyList<AcpPromptAttachment>? attachments = null,
        CancellationToken cancellationToken = default);
    Task CancelAsync();
    Task SetModeAsync(string modeId, CancellationToken cancellationToken = default);
    Task SetModelAsync(string modelId, CancellationToken cancellationToken = default);
    Task SetConfigOptionAsync(string configId, string value, CancellationToken cancellationToken = default);
    Task SetBooleanConfigOptionAsync(string configId, bool value, CancellationToken cancellationToken = default);

    Task AuthenticateAsync(string methodId, IReadOnlyDictionary<string, string>? meta = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    Task LogoutAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    Task<AcpSessionListResult> ListSessionsAsync(string? workingDirectory = null, string? cursor = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AcpSessionListResult());
    Task<AcpSessionStartResult> ForkSessionAsync(string? workingDirectory = null, string? cursorId = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("ACP session/fork is not supported by this session.");
    Task CloseSessionAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    Task ArchiveSessionAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
    AcpTerminalSnapshot? TryGetTerminalSnapshot(string terminalId) => null;
}

/// <summary>Creates a live <see cref="IAcpSession"/> for a resolved launch plan.</summary>
public interface IAcpSessionFactory
{
    IAcpSession Create(AcpLaunchPlan plan, AcpSessionCallbacks callbacks);
}

public sealed class AcpSessionCallbacks
{
    public int PreferredProtocolVersion { get; init; } = AcpProtocolConstants.ProtocolVersion;
    public bool EnableFormElicitation { get; init; } = true;
    public bool EnableUrlElicitation { get; init; } = true;
    public bool EnableClientFileSystem { get; init; }
    public bool EnableClientTerminal { get; init; }
    public bool EnableTerminalAuth { get; init; } = true;
    public IReadOnlyList<AcpMcpServerConfig> McpServers { get; init; } = [];
    public IReadOnlyList<string>? AllowedFileSystemRoots { get; init; }
    public Func<string, string?>? TryReadUnsavedBuffer { get; init; }
    public Action<string, string>? OnFileWritten { get; init; }
    public Func<string>? GetActivePermissionMode { get; init; }
    public Func<AcpPermissionRequest, CancellationToken, Task<string?>>? OnPermissionRequest { get; init; }
    public Func<AcpQuestionRequest, CancellationToken, Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?>>? OnQuestionRequest { get; init; }
    public Func<AcpQuestionRequest, CancellationToken, Task<AcpFormElicitationResponse>>? OnFormElicitationRequest { get; init; }
    public Func<AcpUrlElicitationRequest, CancellationToken, Task<AcpElicitationAction>>? OnUrlElicitationRequest { get; init; }
}

/// <summary>Production factory spawning local stdio ACP subprocesses or remote WebSocket / Streamable HTTP ACP sessions.</summary>
public sealed class AcpProcessSessionFactory(AcpDialectRegistry dialects) : IAcpSessionFactory
{
    public IAcpSession Create(AcpLaunchPlan plan, AcpSessionCallbacks callbacks)
    {
        var dialect = dialects.Resolve(plan.DialectId);
        if (AcpLaunchResolver.IsRemoteTransport(plan.Transport))
        {
            return new AcpRemoteSession(plan, dialect, callbacks);
        }
        return new AcpProcessSession(plan, dialect, callbacks);
    }
}

/// <summary>Owns a child ACP OS process, captures its stderr ring buffer, and drives <see cref="AcpSessionRuntime"/>.</summary>
public sealed class AcpProcessSession : IAcpSession
{
    private const int MaxStderrChars = 64 * 1024;

    private readonly AcpLaunchPlan _plan;
    private readonly IAcpDialect _dialect;
    private readonly AcpSessionCallbacks _callbacks;
    private readonly StringBuilder _stderr = new();
    private readonly object _stderrGate = new();
    private System.Diagnostics.Process? _process;
    private AcpSessionRuntime? _runtime;
    private AcpTerminalHandler? _terminalHandler;
    private bool _disposed;

    public AcpProcessSession(AcpLaunchPlan plan, IAcpDialect dialect, AcpSessionCallbacks callbacks)
    {
        _plan = plan;
        _dialect = dialect;
        _callbacks = callbacks;
    }

    public event Action<AcpRuntimeEvent>? EventReceived;

    public bool IsConnected => _runtime is { IsClosed: false } && _process is { HasExited: false };
    public string? SessionId => _runtime?.SessionId;
    public AcpInitializeResult? Initialized => _runtime?.Initialized;
    public AcpSessionModeState? Modes => _runtime?.Modes;
    public IReadOnlyList<AcpSessionConfigOption> ConfigOptions => _runtime?.ConfigOptions ?? [];
    public AcpSessionModelState? Models => _runtime?.Models;
    public IReadOnlyList<AcpAvailableCommand> AvailableCommands => _runtime?.AvailableCommands ?? [];
    public IAcpDialect Dialect => _dialect;
    public string SourceLabel => _plan.SourceLabel;

    public string GetStderrSnapshot()
    {
        lock (_stderrGate)
        {
            return _stderr.ToString();
        }
    }

    public AcpTerminalSnapshot? TryGetTerminalSnapshot(string terminalId) =>
        _terminalHandler?.TryGetSnapshot(terminalId);

    public async Task<AcpSessionStartResult> StartAsync(
        string workingDirectory,
        IReadOnlyList<string>? additionalDirectories = null,
        string? resumeSessionId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var effectiveCwd = Directory.Exists(workingDirectory) ? workingDirectory : _plan.WorkingDirectory;
        var psi = new ProcessStartInfo
        {
            FileName = _plan.Executable,
            WorkingDirectory = effectiveCwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in _plan.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment.Clear();
        foreach (var (k, v) in _plan.Environment)
        {
            psi.Environment[k] = v;
        }
        foreach (var unset in _plan.UnsetEnvironment)
        {
            psi.Environment.Remove(unset);
        }

        var proc = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!proc.Start())
        {
            throw new InvalidOperationException($"Failed to start ACP process '{_plan.Executable}'.");
        }
        _process = proc;
        var stderrTask = Task.Run(() => PumpStderrAsync(proc.StandardError));

        AcpFileSystemHandler? fsHandler = null;
        if (_callbacks.EnableClientFileSystem)
        {
            fsHandler = new AcpFileSystemHandler(
                _callbacks.AllowedFileSystemRoots,
                _callbacks.TryReadUnsavedBuffer,
                _callbacks.OnFileWritten);
        }
        if (_callbacks.EnableClientTerminal)
        {
            _terminalHandler = new AcpTerminalHandler(effectiveCwd, _plan.Environment);
        }

        var runtime = new AcpSessionRuntime(
            proc.StandardOutput.BaseStream,
            proc.StandardInput.BaseStream,
            new AcpSessionRuntimeOptions
            {
                Dialect = _dialect,
                PermissionStrategy = _plan.PermissionStrategy,
                PreferredProtocolVersion = _callbacks.PreferredProtocolVersion,
                EnableFormElicitation = _callbacks.EnableFormElicitation,
                EnableUrlElicitation = _callbacks.EnableUrlElicitation,
                EnableClientFileSystem = _callbacks.EnableClientFileSystem,
                EnableClientTerminal = _callbacks.EnableClientTerminal,
                EnableTerminalAuth = _callbacks.EnableTerminalAuth,
                AgentVersion = _plan.AgentVersion,
                InitializeTimeout = _plan.InitializeTimeout ?? TimeSpan.FromSeconds(60),
                Environment = _plan.Environment,
                McpServers = _callbacks.McpServers,
                FileSystemHandler = fsHandler,
                TerminalHandler = _terminalHandler,
                GetActivePermissionMode = _callbacks.GetActivePermissionMode,
                OnPermissionRequest = _callbacks.OnPermissionRequest,
                OnQuestionRequest = _callbacks.OnQuestionRequest,
                OnFormElicitationRequest = _callbacks.OnFormElicitationRequest,
                OnUrlElicitationRequest = _callbacks.OnUrlElicitationRequest,
            });
        runtime.EventReceived += evt => EventReceived?.Invoke(evt);
        _runtime = runtime;

        try
        {
            return await runtime.StartSessionAsync(workingDirectory, additionalDirectories, resumeSessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await DisposeAsync().ConfigureAwait(false);
            try { await stderrTask.WaitAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false); } catch { }
            var stderr = GetStderrSnapshot().Trim();
            if (!string.IsNullOrWhiteSpace(stderr) && !ex.Message.Contains(stderr, StringComparison.Ordinal) && ex is not AcpAuthRequiredException)
            {
                var tail = string.Join("\n", stderr.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(6));
                throw new AcpException($"{ex.Message}\n{tail}", ex);
            }
            throw;
        }
    }

    public Task<AcpPromptResult> PromptAsync(
        string text,
        IReadOnlyList<AcpPromptAttachment>? attachments = null,
        CancellationToken cancellationToken = default) =>
        RequireRuntime().PromptAsync(text, attachments, cancellationToken);

    public Task CancelAsync() => _runtime?.CancelAsync() ?? Task.CompletedTask;

    public Task SetModeAsync(string modeId, CancellationToken cancellationToken = default) =>
        RequireRuntime().SetModeAsync(modeId, cancellationToken);

    public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) =>
        RequireRuntime().SetModelAsync(modelId, cancellationToken);

    public Task SetConfigOptionAsync(string configId, string value, CancellationToken cancellationToken = default) =>
        RequireRuntime().SetConfigOptionAsync(configId, value, cancellationToken);

    public Task SetBooleanConfigOptionAsync(string configId, bool value, CancellationToken cancellationToken = default) =>
        RequireRuntime().SetBooleanConfigOptionAsync(configId, value, cancellationToken);

    public Task AuthenticateAsync(string methodId, IReadOnlyDictionary<string, string>? meta = null, CancellationToken cancellationToken = default) =>
        RequireRuntime().AuthenticateAsync(methodId, meta, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken = default) =>
        RequireRuntime().LogoutAsync(cancellationToken);

    public Task<AcpSessionListResult> ListSessionsAsync(string? workingDirectory = null, string? cursor = null, CancellationToken cancellationToken = default) =>
        RequireRuntime().ListSessionsAsync(workingDirectory, cursor, cancellationToken);

    public Task<AcpSessionStartResult> ForkSessionAsync(string? workingDirectory = null, string? cursorId = null, CancellationToken cancellationToken = default) =>
        RequireRuntime().ForkSessionAsync(sessionId: null, workingDirectory: workingDirectory, cursorId: cursorId, cancellationToken: cancellationToken);

    public Task CloseSessionAsync(CancellationToken cancellationToken = default) =>
        _runtime?.CloseSessionAsync(cancellationToken: cancellationToken) ?? Task.CompletedTask;

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        RequireRuntime().DeleteSessionAsync(sessionId, cancellationToken);

    public Task ArchiveSessionAsync(CancellationToken cancellationToken = default) =>
        RequireRuntime().ArchiveSessionAsync(cancellationToken: cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_runtime is { } rt)
        {
            try { await rt.CancelAsync().ConfigureAwait(false); } catch { }
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await rt.CloseSessionAsync(cancellationToken: closeCts.Token).ConfigureAwait(false);
            }
            catch { }
            rt.DrainNotifications();
            if (_process is { HasExited: false } activeProc)
            {
                try { activeProc.StandardInput.Close(); } catch { }
                try
                {
                    using var drainCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                    await activeProc.WaitForExitAsync(drainCts.Token).ConfigureAwait(false);
                }
                catch { }
            }
            await rt.DisposeAsync().ConfigureAwait(false);
        }
        if (_terminalHandler is { } termHandler)
        {
            try { await termHandler.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        if (_process is { } proc)
        {
            try { proc.StandardInput.Close(); } catch { }
            try
            {
                if (!proc.HasExited)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    {
                        try { proc.Kill(entireProcessTree: true); } catch { }
                    }
                }
            }
            catch { }
            proc.Dispose();
        }
    }

    private AcpSessionRuntime RequireRuntime() =>
        _runtime ?? throw new InvalidOperationException("ACP session has not been started.");

    private async Task PumpStderrAsync(StreamReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (_stderrGate)
                {
                    if (_stderr.Length + line.Length + 1 > MaxStderrChars)
                    {
                        var trim = Math.Min(_stderr.Length, MaxStderrChars / 2);
                        _stderr.Remove(0, trim);
                    }
                    _stderr.AppendLine(line);
                }
            }
        }
        catch { /* Stream closed on exit. */ }
    }
}

/// <summary>Remote ACP session communicating over WebSocket (<c>ws</c>/<c>wss</c>) or Streamable HTTP (<c>http</c>/<c>https</c>).</summary>
public sealed class AcpRemoteSession : IAcpSession
{
    private readonly AcpLaunchPlan _plan;
    private readonly IAcpDialect _dialect;
    private readonly AcpSessionCallbacks _callbacks;
    private IAcpTransport? _transport;
    private AcpSessionRuntime? _runtime;
    private AcpTerminalHandler? _terminalHandler;
    private bool _disposed;

    public AcpRemoteSession(AcpLaunchPlan plan, IAcpDialect dialect, AcpSessionCallbacks callbacks)
    {
        _plan = plan;
        _dialect = dialect;
        _callbacks = callbacks;
    }

    public event Action<AcpRuntimeEvent>? EventReceived;

    public bool IsConnected => _runtime is { IsClosed: false };
    public string? SessionId => _runtime?.SessionId;
    public AcpInitializeResult? Initialized => _runtime?.Initialized;
    public AcpSessionModeState? Modes => _runtime?.Modes;
    public IReadOnlyList<AcpSessionConfigOption> ConfigOptions => _runtime?.ConfigOptions ?? [];
    public AcpSessionModelState? Models => _runtime?.Models;
    public IReadOnlyList<AcpAvailableCommand> AvailableCommands => _runtime?.AvailableCommands ?? [];
    public IAcpDialect Dialect => _dialect;
    public string SourceLabel => _plan.SourceLabel;

    public string GetStderrSnapshot() => "";

    public AcpTerminalSnapshot? TryGetTerminalSnapshot(string terminalId) =>
        _terminalHandler?.TryGetSnapshot(terminalId);

    public async Task<AcpSessionStartResult> StartAsync(
        string workingDirectory,
        IReadOnlyList<string>? additionalDirectories = null,
        string? resumeSessionId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(_plan.Endpoint) || !Uri.TryCreate(_plan.Endpoint, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Remote ACP endpoint URL is missing or invalid.");
        }

        if (string.Equals(_plan.Transport, "ws", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_plan.Transport, "websocket", StringComparison.OrdinalIgnoreCase))
        {
            var ws = new AcpWebSocketTransport(uri, _plan.Headers);
            await ws.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _transport = ws;
        }
        else
        {
            _transport = new AcpStreamableHttpTransport(uri, _plan.Headers);
        }

        AcpFileSystemHandler? fsHandler = null;
        if (_callbacks.EnableClientFileSystem)
        {
            fsHandler = new AcpFileSystemHandler(
                _callbacks.AllowedFileSystemRoots,
                _callbacks.TryReadUnsavedBuffer,
                _callbacks.OnFileWritten);
        }
        if (_callbacks.EnableClientTerminal)
        {
            _terminalHandler = new AcpTerminalHandler(workingDirectory, _plan.Environment);
        }

        var runtime = new AcpSessionRuntime(
            _transport,
            new AcpSessionRuntimeOptions
            {
                Dialect = _dialect,
                PermissionStrategy = _plan.PermissionStrategy,
                PreferredProtocolVersion = _callbacks.PreferredProtocolVersion,
                EnableFormElicitation = _callbacks.EnableFormElicitation,
                EnableUrlElicitation = _callbacks.EnableUrlElicitation,
                EnableClientFileSystem = _callbacks.EnableClientFileSystem,
                EnableClientTerminal = _callbacks.EnableClientTerminal,
                EnableTerminalAuth = _callbacks.EnableTerminalAuth,
                AgentVersion = _plan.AgentVersion,
                InitializeTimeout = _plan.InitializeTimeout ?? TimeSpan.FromSeconds(60),
                Environment = _plan.Environment,
                McpServers = _callbacks.McpServers,
                FileSystemHandler = fsHandler,
                TerminalHandler = _terminalHandler,
                GetActivePermissionMode = _callbacks.GetActivePermissionMode,
                OnPermissionRequest = _callbacks.OnPermissionRequest,
                OnQuestionRequest = _callbacks.OnQuestionRequest,
                OnFormElicitationRequest = _callbacks.OnFormElicitationRequest,
                OnUrlElicitationRequest = _callbacks.OnUrlElicitationRequest,
            });
        runtime.EventReceived += evt => EventReceived?.Invoke(evt);
        _runtime = runtime;

        try
        {
            return await runtime.StartSessionAsync(workingDirectory, additionalDirectories, resumeSessionId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<AcpPromptResult> PromptAsync(
        string text,
        IReadOnlyList<AcpPromptAttachment>? attachments = null,
        CancellationToken cancellationToken = default) =>
        RequireRuntime().PromptAsync(text, attachments, cancellationToken);

    public Task CancelAsync() => _runtime?.CancelAsync() ?? Task.CompletedTask;

    public Task SetModeAsync(string modeId, CancellationToken cancellationToken = default) =>
        RequireRuntime().SetModeAsync(modeId, cancellationToken);

    public Task SetModelAsync(string modelId, CancellationToken cancellationToken = default) =>
        RequireRuntime().SetModelAsync(modelId, cancellationToken);

    public Task SetConfigOptionAsync(string configId, string value, CancellationToken cancellationToken = default) =>
        RequireRuntime().SetConfigOptionAsync(configId, value, cancellationToken);

    public Task SetBooleanConfigOptionAsync(string configId, bool value, CancellationToken cancellationToken = default) =>
        RequireRuntime().SetBooleanConfigOptionAsync(configId, value, cancellationToken);

    public Task AuthenticateAsync(string methodId, IReadOnlyDictionary<string, string>? meta = null, CancellationToken cancellationToken = default) =>
        RequireRuntime().AuthenticateAsync(methodId, meta, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken = default) =>
        RequireRuntime().LogoutAsync(cancellationToken);

    public Task<AcpSessionListResult> ListSessionsAsync(string? workingDirectory = null, string? cursor = null, CancellationToken cancellationToken = default) =>
        RequireRuntime().ListSessionsAsync(workingDirectory, cursor, cancellationToken);

    public Task<AcpSessionStartResult> ForkSessionAsync(string? workingDirectory = null, string? cursorId = null, CancellationToken cancellationToken = default) =>
        RequireRuntime().ForkSessionAsync(sessionId: null, workingDirectory: workingDirectory, cursorId: cursorId, cancellationToken: cancellationToken);

    public Task CloseSessionAsync(CancellationToken cancellationToken = default) =>
        _runtime?.CloseSessionAsync(cancellationToken: cancellationToken) ?? Task.CompletedTask;

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        RequireRuntime().DeleteSessionAsync(sessionId, cancellationToken);

    public Task ArchiveSessionAsync(CancellationToken cancellationToken = default) =>
        RequireRuntime().ArchiveSessionAsync(cancellationToken: cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_runtime is { } rt)
        {
            try { await rt.CancelAsync().ConfigureAwait(false); } catch { }
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await rt.CloseSessionAsync(cancellationToken: closeCts.Token).ConfigureAwait(false);
            }
            catch { }
            rt.DrainNotifications();
            await rt.DisposeAsync().ConfigureAwait(false);
        }
        if (_terminalHandler is { } termHandler)
        {
            try { await termHandler.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    private AcpSessionRuntime RequireRuntime() =>
        _runtime ?? throw new InvalidOperationException("ACP remote session has not been started.");
}
