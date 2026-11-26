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

    /// <summary>Applicable install recipes for this platform, in order of preference.</summary>
    public IReadOnlyList<LanguageServerInstallRecipe> ApplicableRecipes =>
        Spec.Install.Where(r => r.AppliesToCurrentPlatform).ToList();
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

/// <summary>Production LSP manager: resolves executables via login PATH, shares one process per workspace, shuts down idle servers after the last tab detaches, and cleans up on disable/exit.</summary>
public sealed class LanguageServerManager : ILanguageServerManager
{
    /// <summary>How long a language server stays warm after its last attached editor closes before being stopped.</summary>
    public static readonly TimeSpan DefaultIdleShutdownDelay = TimeSpan.FromSeconds(45);

    private readonly ISettingsStore _settings;
    private readonly IExecutableLocator _locator;
    private readonly ILoginEnvironment _loginEnvironment;
    private readonly TimeSpan _idleShutdownDelay;
    private readonly ConcurrentDictionary<(string ServerId, string Root), Lazy<Task<RunningServer?>>> _servers = new();
    private bool _disposed;

    public LanguageServerManager(ISettingsStore settings, IExecutableLocator locator, ILoginEnvironment loginEnvironment)
        : this(settings, locator, loginEnvironment, DefaultIdleShutdownDelay)
    {
    }

    internal LanguageServerManager(
        ISettingsStore settings,
        IExecutableLocator locator,
        ILoginEnvironment loginEnvironment,
        TimeSpan idleShutdownDelay)
    {
        _settings = settings;
        _locator = locator;
        _loginEnvironment = loginEnvironment;
        _idleShutdownDelay = idleShutdownDelay;
        _settings.Changed += OnSettingsChanged;
    }

    internal int RunningServerCount => _servers.Count;

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

        RunningServer? running = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (entry, candidate) = await GetOrStartAsync(key, chosen.Value.Spec, chosen.Value.ExecutablePath, root, cancellationToken).ConfigureAwait(false);
            if (candidate is null || entry is null) return null;
            if (candidate.TryAcquire())
            {
                running = candidate;
                break;
            }
            // Raced with idle eviction; remove stale entry and retry once.
            _servers.TryRemove(new KeyValuePair<(string ServerId, string Root), Lazy<Task<RunningServer?>>>(key, entry));
        }
        if (running is null) return null;

        // Re-check cancellation, disposal, and settings after acquiring an attachment refcount.
        var current = _settings.Current.Experiments;
        if (cancellationToken.IsCancellationRequested || _disposed || !current.CodeEditor || !current.LanguageServer)
        {
            running.Release();
            return null;
        }

        IDisposable? inner;
        try
        {
            var uri = new Uri(fullFile);
            inner = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested || _disposed || !running.Client.IsRunning) return null;
                return editor.AttachLanguageClient(running.Client, uri, languageId);
            });
        }
        catch
        {
            running.Release();
            throw;
        }
        if (inner is null)
        {
            running.Release();
            return null;
        }
        return new AttachmentHandle(inner, running.Release);
    }

    private async Task<(Lazy<Task<RunningServer?>>? Entry, RunningServer? Server)> GetOrStartAsync(
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
                _servers.TryRemove(new KeyValuePair<(string ServerId, string Root), Lazy<Task<RunningServer?>>>(key, lazy));
                return (null, null);
            }
            return (lazy, server);
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }
        catch (Exception)
        {
            _servers.TryRemove(new KeyValuePair<(string ServerId, string Root), Lazy<Task<RunningServer?>>>(key, lazy));
            return (null, null);
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
            if (await GetSearchPathAsync().ConfigureAwait(false) is { } searchPath) start.Environment["PATH"] = searchPath;

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

            using var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.StartAsync(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, startTimeout.Token).ConfigureAwait(false);
            if (_disposed || !_settings.Current.Experiments.LanguageServer)
            {
                DisposeServer(client, process);
                return null;
            }
            var running = new RunningServer(client, process, _idleShutdownDelay, server => EvictAndDispose(key, server));
            client.Disconnected += (_, _) => EvictAndDispose(key, running);
            return running;
        }
        catch (Exception)
        {
            DisposeServer(client, process);
            return null;
        }
    }

    private void EvictAndDispose((string ServerId, string Root) key, RunningServer server)
    {
        if (_servers.TryGetValue(key, out var existing)
            && existing.IsValueCreated
            && existing.Value.IsCompletedSuccessfully
            && ReferenceEquals(existing.Value.Result, server))
        {
            _servers.TryRemove(new KeyValuePair<(string ServerId, string Root), Lazy<Task<RunningServer?>>>(key, existing));
        }
        server.Dispose();
    }

    /// <summary>
    /// The login PATH plus the per-user bin folders that tool installers write to but shell profiles often do not
    /// add (dotnet global tools, go install, cargo/rustup, pipx), so a freshly installed server is found at once.
    /// </summary>
    private async Task<string?> GetSearchPathAsync(CancellationToken cancellationToken = default)
    {
        var env = await _loginEnvironment.GetAsync(cancellationToken).ConfigureAwait(false);
        var path = env.TryGetValue("PATH", out var login) && login.Length > 0 ? login : Environment.GetEnvironmentVariable("PATH");
        var entries = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var extra in ExtraBinDirectories(env))
        {
            if (!entries.Contains(extra, comparer)) entries.Add(extra);
        }
        return entries.Count == 0 ? null : string.Join(Path.PathSeparator, entries);
    }

    private static IEnumerable<string> ExtraBinDirectories(IReadOnlyDictionary<string, string> env)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) yield break;
        yield return Path.Combine(home, ".dotnet", "tools");
        var goPath = env.TryGetValue("GOPATH", out var gp)
            ? gp.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? Path.Combine(home, "go")
            : Path.Combine(home, "go");
        yield return env.TryGetValue("GOBIN", out var goBin) && goBin.Length > 0 ? goBin : Path.Combine(goPath, "bin");
        yield return Path.Combine(home, ".cargo", "bin");
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (programFiles.Length > 0) yield return Path.Combine(programFiles, "LLVM", "bin");
        }
        else
        {
            yield return Path.Combine(home, ".local", "bin");
        }
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

    internal sealed class AttachmentHandle(IDisposable inner, Action onDispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { inner.Dispose(); } catch (Exception) { }
            onDispose();
        }
    }

    internal sealed class RunningServer : IDisposable
    {
        private readonly object _gate = new();
        private readonly Func<bool> _isAlive;
        private readonly Action _disposeCore;
        private readonly TimeSpan _idleDelay;
        private readonly Action<RunningServer> _onIdle;
        private int _activeAttachments;
        private Timer? _idleTimer;
        private bool _closing;
        private int _disposed;

        public RunningServer(LanguageClient client, Process process, TimeSpan idleDelay, Action<RunningServer> onIdle)
            : this(client, () => client.IsRunning, () => DisposeServer(client, process), idleDelay, onIdle)
        {
        }

        internal RunningServer(Func<bool> isAlive, TimeSpan idleDelay, Action<RunningServer> onIdle, Action disposeCore)
            : this(null!, isAlive, disposeCore, idleDelay, onIdle)
        {
        }

        private RunningServer(
            LanguageClient client,
            Func<bool> isAlive,
            Action disposeCore,
            TimeSpan idleDelay,
            Action<RunningServer> onIdle)
        {
            Client = client;
            _isAlive = isAlive;
            _disposeCore = disposeCore;
            _idleDelay = idleDelay;
            _onIdle = onIdle;
            if (idleDelay > TimeSpan.Zero && idleDelay != Timeout.InfiniteTimeSpan)
            {
                ArmIdleTimerLocked();
            }
        }

        public LanguageClient Client { get; }

        public bool TryAcquire()
        {
            lock (_gate)
            {
                if (_closing || !_isAlive()) return false;
                _idleTimer?.Dispose();
                _idleTimer = null;
                _activeAttachments++;
                return true;
            }
        }

        public void Release()
        {
            var idleNow = false;
            lock (_gate)
            {
                if (_closing) return;
                if (_activeAttachments > 0) _activeAttachments--;
                if (_activeAttachments == 0)
                {
                    if (_idleDelay <= TimeSpan.Zero)
                    {
                        _closing = true;
                        _idleTimer?.Dispose();
                        _idleTimer = null;
                        idleNow = true;
                    }
                    else
                    {
                        ArmIdleTimerLocked();
                    }
                }
            }
            if (idleNow) _onIdle(this);
        }

        private void ArmIdleTimerLocked()
        {
            if (_closing || _idleDelay <= TimeSpan.Zero || _idleDelay == Timeout.InfiniteTimeSpan) return;
            _idleTimer?.Dispose();
            _idleTimer = new Timer(static state => ((RunningServer)state!).OnIdleElapsed(), this, _idleDelay, Timeout.InfiniteTimeSpan);
        }

        private void OnIdleElapsed()
        {
            lock (_gate)
            {
                if (_closing || _activeAttachments > 0) return;
                _closing = true;
                _idleTimer?.Dispose();
                _idleTimer = null;
            }
            _onIdle(this);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _closing = true;
                _idleTimer?.Dispose();
                _idleTimer = null;
            }
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _disposeCore();
            }
        }
    }
}
