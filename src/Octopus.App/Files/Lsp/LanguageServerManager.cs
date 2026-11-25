using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Threading;
using Octopus.Platform;
using Octopus.Storage;
using TKCode.Arc;
using TKCode.Arc.Editor.Lsp;

namespace Octopus.App.Files.Lsp;

/// <summary>Result of probing whether a known local language server executable is installed on PATH.</summary>
public sealed record DetectedLanguageServer(LanguageServerSpec Spec, string? ExecutablePath)
{
    public bool IsInstalled => ExecutablePath is not null;
}

/// <summary>
/// Manages optional local Language Server Protocol (LSP) processes and attaches them to <see cref="CodeEditor"/> tabs.
/// Controlled by <c>Experiments.LanguageServer</c>; spawns nothing while disabled.
/// </summary>
public interface ILanguageServerManager : IDisposable
{
    /// <summary>Probes PATH for every known server in <see cref="LanguageServerCatalog.All"/> without launching any process.</summary>
    Task<IReadOnlyList<DetectedLanguageServer>> DetectInstalledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches <paramref name="editor"/> to a matching local language server for <paramref name="languageId"/> in
    /// <paramref name="workspaceRoot"/>, lazily starting one shared server per (server, workspace). Returns null when
    /// LSP is disabled, unsupported, or not installed. Dispose the returned handle to detach.
    /// </summary>
    Task<IDisposable?> AttachAsync(
        CodeEditor editor,
        string filePath,
        string languageId,
        string? workspaceRoot,
        CancellationToken cancellationToken = default);

    /// <summary>Stops every running language server process.</summary>
    void StopAll();
}

/// <summary>Production LSP manager: resolves executables via login PATH, shares one process per workspace, and cleans up on disable/exit.</summary>
public sealed class LanguageServerManager : ILanguageServerManager
{
    private readonly ISettingsStore _settings;
    private readonly IExecutableLocator _locator;
    private readonly ILoginEnvironment _loginEnvironment;
    private readonly ConcurrentDictionary<(string ServerId, string Root), Lazy<Task<RunningServer?>>> _servers = new();
    private bool _disposed;

    public LanguageServerManager(ISettingsStore settings, IExecutableLocator locator, ILoginEnvironment loginEnvironment)
    {
        _settings = settings;
        _locator = locator;
        _loginEnvironment = loginEnvironment;
        _settings.Changed += OnSettingsChanged;
    }

    public async Task<IReadOnlyList<DetectedLanguageServer>> DetectInstalledAsync(CancellationToken cancellationToken = default)
    {
        var path = await GetSearchPathAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var results = new List<DetectedLanguageServer>(LanguageServerCatalog.All.Count);
            foreach (var spec in LanguageServerCatalog.All)
            {
                var found = _locator.FindAll(spec.Executable, path).FirstOrDefault();
                results.Add(new DetectedLanguageServer(spec, found));
            }
            return (IReadOnlyList<DetectedLanguageServer>)results;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IDisposable?> AttachAsync(
        CodeEditor editor,
        string filePath,
        string languageId,
        string? workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(languageId)) return null;
        var experiments = _settings.Current.Experiments;
        if (!experiments.CodeEditor || !experiments.LanguageServer) return null;

        var candidates = LanguageServerCatalog.ForLanguage(languageId).ToList();
        if (candidates.Count == 0) return null;

        var searchPath = await GetSearchPathAsync(cancellationToken).ConfigureAwait(false);
        (LanguageServerSpec Spec, string ExecutablePath)? chosen = null;
        foreach (var spec in candidates)
        {
            var found = _locator.FindAll(spec.Executable, searchPath).FirstOrDefault();
            if (found is not null)
            {
                chosen = (spec, found);
                break;
            }
        }
        if (chosen is null || cancellationToken.IsCancellationRequested || _disposed) return null;

        var fullFile = Path.GetFullPath(filePath);
        var root = string.IsNullOrWhiteSpace(workspaceRoot)
            ? Path.GetDirectoryName(fullFile) ?? fullFile
            : Path.GetFullPath(workspaceRoot);
        var key = (chosen.Value.Spec.Id, root);

        var running = await GetOrStartAsync(key, chosen.Value.Spec, chosen.Value.ExecutablePath, root, cancellationToken).ConfigureAwait(false);
        if (running is null || !running.Client.IsRunning || cancellationToken.IsCancellationRequested || _disposed) return null;

        // Re-check setting in case user disabled LSP while the server was starting.
        var current = _settings.Current.Experiments;
        if (!current.CodeEditor || !current.LanguageServer) return null;

        var uri = new Uri(fullFile);
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested || _disposed || !running.Client.IsRunning) return (IDisposable?)null;
            return editor.AttachLanguageClient(running.Client, uri, languageId);
        });
    }

    private async Task<RunningServer?> GetOrStartAsync(
        (string ServerId, string Root) key,
        LanguageServerSpec spec,
        string executablePath,
        string root,
        CancellationToken cancellationToken)
    {
        var lazy = _servers.GetOrAdd(key, _ => new Lazy<Task<RunningServer?>>(() => StartServerAsync(key, spec, executablePath, root)));
        try
        {
            var server = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (server is null || !server.Client.IsRunning)
            {
                _servers.TryRemove(key, out _);
                return null;
            }
            return server;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            _servers.TryRemove(key, out _);
            return null;
        }
    }

    private async Task<RunningServer?> StartServerAsync(
        (string ServerId, string Root) key,
        LanguageServerSpec spec,
        string executablePath,
        string root)
    {
        Process? process = null;
        LanguageClient? client = null;
        try
        {
            var env = await _loginEnvironment.GetAsync().ConfigureAwait(false);
            var start = new ProcessStartInfo(executablePath)
            {
                WorkingDirectory = Directory.Exists(root) ? root : Path.GetTempPath(),
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in spec.Arguments) start.ArgumentList.Add(arg);
            foreach (var (k, v) in env) start.Environment[k] = v;

            process = Process.Start(start);
            if (process is null) return null;
            // Drain stderr asynchronously so verbose servers never block on a full stderr pipe.
            process.BeginErrorReadLine();

            var rootUri = new Uri(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar);
            client = new LanguageClient(new LanguageClientOptions
            {
                Name = spec.Id,
                ClientName = "Octopus",
                RootUri = rootUri,
                ShutdownTimeout = TimeSpan.FromSeconds(2),
            });
            client.Disconnected += (_, _) => _servers.TryRemove(key, out _);

            using var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.StartAsync(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, startTimeout.Token).ConfigureAwait(false);
            if (_disposed || !_settings.Current.Experiments.LanguageServer)
            {
                DisposeServer(client, process);
                return null;
            }
            return new RunningServer(client, process);
        }
        catch (Exception)
        {
            DisposeServer(client, process);
            return null;
        }
    }

    private async Task<string?> GetSearchPathAsync(CancellationToken cancellationToken)
    {
        var env = await _loginEnvironment.GetAsync(cancellationToken).ConfigureAwait(false);
        return env.TryGetValue("PATH", out var path) && path.Length > 0 ? path : Environment.GetEnvironmentVariable("PATH");
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var exp = _settings.Current.Experiments;
        if (!exp.CodeEditor || !exp.LanguageServer) StopAll();
    }

    public void StopAll()
    {
        foreach (var entry in _servers.ToArray())
        {
            if (!_servers.TryRemove(entry.Key, out var lazy)) continue;
            if (!lazy.IsValueCreated) continue;
            _ = lazy.Value.ContinueWith(static t =>
            {
                if (t.IsCompletedSuccessfully && t.Result is { } running) running.Dispose();
            }, TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        StopAll();
    }

    private static void DisposeServer(LanguageClient? client, Process? process)
    {
        try { client?.Dispose(); } catch (Exception) { }
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
        try { process.Dispose(); } catch (Exception) { }
    }

    private sealed class RunningServer(LanguageClient client, Process process) : IDisposable
    {
        public LanguageClient Client { get; } = client;

        public void Dispose() => DisposeServer(Client, process);
    }
}
