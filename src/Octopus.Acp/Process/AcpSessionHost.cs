using System.Diagnostics;
using System.Text;
using Octopus.Acp.Dialects;
using Octopus.Acp.Protocol;

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
}

/// <summary>Creates a live <see cref="IAcpSession"/> for a resolved launch plan.</summary>
public interface IAcpSessionFactory
{
    IAcpSession Create(AcpLaunchPlan plan, AcpSessionCallbacks callbacks);
}

public sealed class AcpSessionCallbacks
{
    public bool EnableFormElicitation { get; init; } = true;
    public Func<string>? GetActivePermissionMode { get; init; }
    public Func<AcpPermissionRequest, CancellationToken, Task<string?>>? OnPermissionRequest { get; init; }
    public Func<AcpQuestionRequest, CancellationToken, Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?>>? OnQuestionRequest { get; init; }
}

/// <summary>Production factory spawning real stdio ACP subprocesses.</summary>
public sealed class AcpProcessSessionFactory(AcpDialectRegistry dialects) : IAcpSessionFactory
{
    public IAcpSession Create(AcpLaunchPlan plan, AcpSessionCallbacks callbacks) =>
        new AcpProcessSession(plan, dialects.Resolve(plan.DialectId), callbacks);
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

    public async Task<AcpSessionStartResult> StartAsync(
        string workingDirectory,
        IReadOnlyList<string>? additionalDirectories = null,
        string? resumeSessionId = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var psi = new ProcessStartInfo
        {
            FileName = _plan.Executable,
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : _plan.WorkingDirectory,
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

        var runtime = new AcpSessionRuntime(
            proc.StandardOutput.BaseStream,
            proc.StandardInput.BaseStream,
            new AcpSessionRuntimeOptions
            {
                Dialect = _dialect,
                PermissionStrategy = _plan.PermissionStrategy,
                EnableFormElicitation = _callbacks.EnableFormElicitation,
                AgentVersion = _plan.AgentVersion,
                InitializeTimeout = _plan.InitializeTimeout ?? TimeSpan.FromSeconds(60),
                Environment = _plan.Environment,
                GetActivePermissionMode = _callbacks.GetActivePermissionMode,
                OnPermissionRequest = _callbacks.OnPermissionRequest,
                OnQuestionRequest = _callbacks.OnQuestionRequest,
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
            if (!string.IsNullOrWhiteSpace(stderr) && !ex.Message.Contains(stderr, StringComparison.Ordinal) && ex is not Octopus.Acp.Transport.AcpAuthRequiredException)
            {
                var tail = string.Join("\n", stderr.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(6));
                throw new Octopus.Acp.Transport.AcpException($"{ex.Message}\n{tail}", ex);
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

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_runtime is { } rt)
        {
            try { await rt.CancelAsync().ConfigureAwait(false); } catch { }
            await rt.DisposeAsync().ConfigureAwait(false);
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
