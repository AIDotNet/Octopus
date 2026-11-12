using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Octopus.Core.Plugins;
using Octopus.Plugins.Catalog;

namespace Octopus.Plugins.Runtime;

public enum PluginHostState
{
    /// <summary>Enabled but its code has not run yet (no activation event so far).</summary>
    Inactive,
    Activating,
    Running,
    /// <summary>The engine faulted; a restart is scheduled.</summary>
    Crashed,
    /// <summary>Crashed too often, or deactivated; needs the user (or a new activation) to start again.</summary>
    Stopped,
    /// <summary>The code could not be loaded or activate() failed; see <see cref="PluginHost.LastError"/>.</summary>
    Failed,
}

public sealed record PluginLogEntry(DateTimeOffset Time, PluginLogLevel Level, string Text);

/// <summary>Raised by <see cref="PluginHost.InvokeAsync"/> when the plugin's handler threw or the plugin is not running.</summary>
public sealed class PluginInvokeException(string code, string message, string? stack = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? PluginStack { get; } = stack;
}

/// <summary>What a host needs from the manager that owns it.</summary>
internal interface IPluginHostOwner
{
    PluginApiRouter Router { get; }
    IPluginRuntimeFactory RuntimeFactory { get; }
    PluginPaths Paths { get; }
    TurnBudgets Budgets { get; }
    void OnFault(PluginHost host, PluginFault fault);
    void OnRegistrationsChanged(PluginHost host);
    void OnStateChanged(PluginHost host);
}

/// <summary>
/// One plugin at run time: its runtime, the calls in flight both ways, its timers, event subscriptions and what it
/// registered (design/plugins/02-运行时.md). Everything the plugin registered is dropped when it stops, whatever
/// the plugin itself cleaned up.
/// </summary>
public sealed class PluginHost : IPluginRuntimeSink, IAsyncDisposable
{
    public const int LogCapacity = 2000;
    public const long LogFileLimit = 2 * 1024 * 1024;
    public static readonly TimeSpan ActivateTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultInvokeTimeout = TimeSpan.FromSeconds(5);

    private readonly IPluginHostOwner _owner;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<string>> _calls = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _requests = new();
    private readonly ConcurrentDictionary<long, Timer> _timers = new();
    private readonly ConcurrentDictionary<string, PluginSubscription> _subscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _commands = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Kind, string Id), byte> _providers = new();
    private readonly LinkedList<PluginLogEntry> _log = new();
    private readonly Channel<PluginLogEntry> _logFile = Channel.CreateUnbounded<PluginLogEntry>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private IPluginRuntime? _runtime;
    private long _nextCall;
    private PluginHostState _state = PluginHostState.Inactive;

    internal PluginHost(PluginPackage package, PluginGrants grants, IPluginHostOwner owner)
    {
        Package = package;
        Manifest = package.Manifest ?? throw new ArgumentException("The package has no manifest.", nameof(package));
        Grants = grants;
        _owner = owner;
        _ = Task.Run(WriteLogFileAsync);
    }

    public string Id => Manifest.Id;
    public PluginPackage Package { get; }
    public PluginManifest Manifest { get; }
    public PluginGrants Grants { get; internal set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }

    public PluginHostState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            _owner.OnStateChanged(this);
        }
    }

    public IReadOnlyCollection<string> Commands => _commands.Keys.ToList();
    public IReadOnlyCollection<(string Kind, string Id)> Providers => _providers.Keys.ToList();
    public IReadOnlyCollection<PluginSubscription> Subscriptions => _subscriptions.Values.ToList();
    public PluginRuntimeStats? Stats => _runtime?.Stats;

    public IReadOnlyList<PluginLogEntry> Log
    {
        get { lock (_log) return _log.ToList(); }
    }

    public event EventHandler<PluginLogEntry>? Logged;

    public string DataDirectory => _owner.Paths.DataDirectoryOf(Id);
    public string TempDirectory => _owner.Paths.TempDirectoryOf(Id);

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Loads the code and runs <c>activate()</c>. Safe to call while already running.</summary>
    internal async Task ActivateAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == PluginHostState.Running || Manifest.Main is null)
            {
                return;
            }
            State = PluginHostState.Activating;
            LastError = null;
            Directory.CreateDirectory(DataDirectory);
            var runtime = _owner.RuntimeFactory.Create();
            try
            {
                var options = new PluginRuntimeOptions(Id, Package.Directory, Manifest.Main, Manifest.UnsafeEval, _owner.Budgets);
                await Task.Run(() => runtime.StartAsync(options, this, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            catch (PluginLoadException e)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
                Fail(e.Line > 0 ? $"{e.SourceName}:{e.Line}:{e.Column} {e.Message}" : e.Message);
                return;
            }
            _runtime = runtime;
            State = PluginHostState.Running;
            try
            {
                Directory.CreateDirectory(_owner.Paths.FilesDirectoryOf(Id));
                Directory.CreateDirectory(TempDirectory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Write(PluginLogLevel.Warn, $"Could not create the plugin's directories: {e.Message}");
            }
            var context = new JsonObject
            {
                ["id"] = Id,
                ["version"] = Manifest.Version,
                ["pluginPath"] = Package.Directory,
                ["dataPath"] = _owner.Paths.FilesDirectoryOf(Id),
                ["tmpPath"] = TempDirectory,
            };
            try
            {
                await CallAsync(InboundKind.Lifecycle, "activate", context.ToJsonString(), ActivateTimeout, cancellationToken).ConfigureAwait(false);
                ActivatedAt = DateTimeOffset.Now;
            }
            catch (Exception e) when (e is PluginInvokeException or TimeoutException)
            {
                await StopRuntimeAsync().ConfigureAwait(false);
                Fail($"activate() failed: {e.Message}");
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Runs <c>deactivate()</c> (2 seconds at most) and stops the runtime.</summary>
    internal async Task DeactivateAsync(PluginHostState after = PluginHostState.Inactive)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == PluginHostState.Running)
            {
                try
                {
                    await CallAsync(InboundKind.Lifecycle, "deactivate", "", TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e) when (e is PluginInvokeException or TimeoutException)
                {
                    Write(PluginLogLevel.Warn, $"deactivate() did not finish cleanly: {e.Message}");
                }
            }
            await StopRuntimeAsync().ConfigureAwait(false);
            State = after;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>After a fault: drop the runtime and everything registered; the owner decides about a restart.</summary>
    internal async Task CrashAsync(string reason)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            LastError = reason;
            await StopRuntimeAsync().ConfigureAwait(false);
            State = PluginHostState.Crashed;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal void MarkStopped(string reason)
    {
        LastError = reason;
        State = PluginHostState.Stopped;
    }

    private void Fail(string message)
    {
        LastError = message;
        Write(PluginLogLevel.Error, message);
        State = PluginHostState.Failed;
    }

    private async Task StopRuntimeAsync()
    {
        var runtime = Interlocked.Exchange(ref _runtime, null);
        foreach (var (_, timer) in _timers) timer.Dispose();
        _timers.Clear();
        foreach (var (_, cts) in _requests) cts.Cancel();
        _requests.Clear();
        foreach (var (_, call) in _calls) call.TrySetException(new PluginInvokeException("Stopped", "The plugin stopped."));
        _calls.Clear();
        foreach (var subscription in _subscriptions.Values)
        {
            foreach (var module in _owner.Router.Modules) module.Unsubscribed(this, subscription);
        }
        _subscriptions.Clear();
        var hadRegistrations = !_commands.IsEmpty || !_providers.IsEmpty;
        _commands.Clear();
        _providers.Clear();
        foreach (var module in _owner.Router.Modules)
        {
            try { module.Reset(this); } catch (Exception e) { Write(PluginLogLevel.Error, $"Reset failed in {module.GetType().Name}: {e.Message}"); }
        }
        if (hadRegistrations) _owner.OnRegistrationsChanged(this);
        if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopRuntimeAsync().ConfigureAwait(false);
        _logFile.Writer.TryComplete();
    }

    // ---------------------------------------------------------------- calling the plugin

    /// <summary>
    /// Runs a handler the plugin registered (a command, a view method…) and returns its result as JSON.
    /// </summary>
    /// <exception cref="PluginInvokeException">The handler threw, or the plugin is not running.</exception>
    /// <exception cref="TimeoutException">No answer within <paramref name="timeout"/>.</exception>
    public Task<string> InvokeAsync(string handler, JsonArray? args = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        CallAsync(InboundKind.Invoke, handler, (args ?? []).ToJsonString(), timeout ?? DefaultInvokeTimeout, cancellationToken);

    private async Task<string> CallAsync(InboundKind kind, string name, string json, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_runtime is not { } runtime || State is not (PluginHostState.Running or PluginHostState.Activating))
        {
            throw new PluginInvokeException("NotRunning", $"The plugin {Id} is not running.");
        }
        var id = Interlocked.Increment(ref _nextCall);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _calls[id] = completion;
        runtime.Post(new PluginInbound(kind, id, name, json));
        try
        {
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"{Id} did not answer \"{name}\" within {timeout.TotalSeconds:0.#} s.");
        }
        finally
        {
            _calls.TryRemove(id, out _);
        }
    }

    /// <summary>Sends an event to the plugin's subscriptions of <paramref name="event"/> whose arguments <paramref name="matches"/>.</summary>
    public void Emit(string @event, JsonNode? payload, Func<JsonNode?, bool>? matches = null)
    {
        if (_runtime is not { } runtime) return;
        string? json = null;
        foreach (var subscription in _subscriptions.Values)
        {
            if (subscription.Event != @event || (matches is not null && !matches(subscription.Args))) continue;
            json ??= payload?.ToJsonString() ?? "null";
            runtime.Post(new PluginInbound(InboundKind.Event, 0, subscription.Key, json));
        }
    }

    /// <summary>Sends an event to one subscription key (when a module tracks the key itself, e.g. a file watcher).</summary>
    public void EmitTo(string key, JsonNode? payload)
    {
        if (_runtime is { } runtime && _subscriptions.ContainsKey(key))
        {
            runtime.Post(new PluginInbound(InboundKind.Event, 0, key, payload?.ToJsonString() ?? "null"));
        }
    }

    public bool IsSubscribed(string @event) => _subscriptions.Values.Any(s => s.Event == @event);

    // ---------------------------------------------------------------- the plugin calling the host

    void IPluginRuntimeSink.OnOutbound(in PluginOutbound message)
    {
        switch (message.Kind)
        {
            case OutboundKind.Result:
                if (_calls.TryGetValue(message.Id, out var done)) done.TrySetResult(message.Json);
                return;
            case OutboundKind.Error:
                if (_calls.TryGetValue(message.Id, out var failed))
                {
                    var error = Parse(message.Json);
                    failed.TrySetException(new PluginInvokeException(
                        error?["code"]?.GetValue<string>() ?? "PluginError",
                        error?["message"]?.GetValue<string>() ?? "The plugin's handler failed.",
                        error?["stack"]?.GetValue<string>()));
                }
                return;
            case OutboundKind.Notify:
                HandleNotify(message.Method, message.Json, message.Text);
                return;
            case OutboundKind.Request:
                StartRequest(message.Id, message.Method, message.Json, message.Text);
                return;
        }
    }

    private void StartRequest(long id, string method, string json, string text)
    {
        var runtime = _runtime;
        if (runtime is null) return;
        var handler = _owner.Router.FindRequest(method);
        if (handler is null)
        {
            runtime.Post(new PluginInbound(InboundKind.Reject, id, "", PluginApiException.Unsupported($"\"{method}\"").ToJson()));
            return;
        }
        var cts = new CancellationTokenSource();
        _requests[id] = cts;
        _ = Task.Run(async () =>
        {
            PluginInbound answer;
            try
            {
                var reply = await handler(new PluginCall(this, method, json, text), cts.Token).ConfigureAwait(false);
                answer = new PluginInbound(InboundKind.Resolve, id, "", reply.Json, reply.Text);
            }
            catch (PluginApiException e)
            {
                answer = new PluginInbound(InboundKind.Reject, id, "", e.ToJson());
            }
            catch (OperationCanceledException)
            {
                answer = new PluginInbound(InboundKind.Reject, id, "", new PluginApiException("Cancelled", "The operation was cancelled.").ToJson());
            }
            catch (Exception e)
            {
                Write(PluginLogLevel.Error, $"{method} failed in the host: {e.GetType().Name}: {e.Message}");
                answer = new PluginInbound(InboundKind.Reject, id, "", new PluginApiException("Internal", $"{method} failed.").ToJson());
            }
            finally
            {
                _requests.TryRemove(id, out _);
                cts.Dispose();
            }
            _runtime?.Post(answer);
        });
    }

    private void HandleNotify(string method, string json, string text)
    {
        var args = Parse(json);
        switch (method)
        {
            case "timer.set":
                SetTimer(args);
                return;
            case "timer.clear":
                if (args?["id"]?.GetValue<double>() is { } clearId && _timers.TryRemove((long)clearId, out var t)) t.Dispose();
                return;
            case "request.cancel":
                if (args?["id"]?.GetValue<double>() is { } requestId && _requests.TryGetValue((long)requestId, out var cts)) cts.Cancel();
                return;
            case "events.subscribe":
                if (args?["key"]?.GetValue<string>() is { } key && args["event"]?.GetValue<string>() is { } ev)
                {
                    var subscription = new PluginSubscription(ev, key, args["args"]?.DeepClone());
                    _subscriptions[key] = subscription;
                    foreach (var module in _owner.Router.Modules) module.Subscribed(this, subscription);
                }
                return;
            case "events.unsubscribe":
                if (args?["key"]?.GetValue<string>() is { } oldKey && _subscriptions.TryRemove(oldKey, out var old))
                {
                    foreach (var module in _owner.Router.Modules) module.Unsubscribed(this, old);
                }
                return;
            case "commands.register":
                if (args?["id"]?.GetValue<string>() is { } command)
                {
                    if (IsOwnCommand(command)) { _commands[command] = 0; _owner.OnRegistrationsChanged(this); }
                    else Write(PluginLogLevel.Warn, $"commands.register: \"{command}\" must be declared in plugin.json contributes.commands");
                }
                return;
            case "commands.unregister":
                if (args?["id"]?.GetValue<string>() is { } gone && _commands.TryRemove(gone, out _)) _owner.OnRegistrationsChanged(this);
                return;
            case "providers.register":
                if (args?["kind"]?.GetValue<string>() is { } kind && args["id"]?.GetValue<string>() is { } providerId)
                {
                    if (IsDeclaredProvider(kind, providerId)) { _providers[(kind, providerId)] = 0; _owner.OnRegistrationsChanged(this); }
                    else Write(PluginLogLevel.Warn, $"{kind} \"{providerId}\" must be declared in plugin.json contributes");
                }
                return;
            case "providers.unregister":
                if (args?["kind"]?.GetValue<string>() is { } k && args["id"]?.GetValue<string>() is { } p && _providers.TryRemove((k, p), out _))
                    _owner.OnRegistrationsChanged(this);
                return;
        }
        if (_owner.Router.FindNotify(method) is { } notify)
        {
            try
            {
                notify(new PluginCall(this, method, json, text));
            }
            catch (PluginApiException e)
            {
                Write(PluginLogLevel.Warn, $"{method}: {e.Message}");
            }
            catch (Exception e)
            {
                Write(PluginLogLevel.Error, $"{method} failed in the host: {e.GetType().Name}: {e.Message}");
            }
        }
        else
        {
            Write(PluginLogLevel.Warn, $"\"{method}\" is not available in this version of Octopus.");
        }
    }

    private bool IsOwnCommand(string command) =>
        Manifest.Contributes.Commands.Any(c => c.Id == command) || Manifest.Contributes.Views.Any(v => $"{v.Id}.open" == command);

    private bool IsDeclaredProvider(string kind, string id)
    {
        var c = Manifest.Contributes;
        return kind switch
        {
            "view" => c.Views.Any(v => v.Id == id),
            "editor" => c.Editors.Any(e => e.Id == id),
            "panel" => c.Panels.Any(p => p.Id == id),
            "sidebarSection" => c.SidebarSections.Any(s => s.Id == id),
            _ => false,
        };
    }

    private void SetTimer(JsonNode? args)
    {
        if (args?["id"]?.GetValue<double>() is not { } rawId) return;
        var id = (long)rawId;
        var ms = Math.Max(16, (long)(args["ms"]?.GetValue<double>() ?? 16));
        var repeat = args["repeat"]?.GetValue<bool>() ?? false;
        if (_timers.Count >= 1000)
        {
            Write(PluginLogLevel.Warn, "More than 1000 active timers; this one is ignored.");
            return;
        }
        var period = repeat ? TimeSpan.FromMilliseconds(ms) : Timeout.InfiniteTimeSpan;
        var timer = new Timer(state =>
        {
            if (!repeat) _timers.TryRemove(id, out _);
            _runtime?.Post(new PluginInbound(InboundKind.Timer, id, "", ""));
        }, null, TimeSpan.FromMilliseconds(ms), period);
        if (_timers.TryGetValue(id, out var previous)) previous.Dispose();
        _timers[id] = timer;
    }

    string IPluginRuntimeSink.OnSync(string method, string json)
    {
        var handler = _owner.Router.FindSync(method);
        if (handler is null) return "";
        try
        {
            return handler(new PluginCall(this, method, json, ""));
        }
        catch (Exception e)
        {
            Write(PluginLogLevel.Warn, $"{method}: {e.Message}");
            return "";
        }
    }

    void IPluginRuntimeSink.OnLog(PluginLogLevel level, string text) => Write(level, text);

    void IPluginRuntimeSink.OnFault(PluginFault fault)
    {
        Write(PluginLogLevel.Error, $"The plugin stopped: {fault.Message}");
        _owner.OnFault(this, fault);
    }

    private static JsonNode? Parse(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // ---------------------------------------------------------------- log

    /// <summary>Adds a line to the plugin's log (memory ring and <c>plugin-data/&lt;id&gt;/log.txt</c>).</summary>
    public void Write(PluginLogLevel level, string text)
    {
        var entry = new PluginLogEntry(DateTimeOffset.Now, level, text);
        lock (_log)
        {
            _log.AddLast(entry);
            while (_log.Count > LogCapacity) _log.RemoveFirst();
        }
        _logFile.Writer.TryWrite(entry);
        Logged?.Invoke(this, entry);
    }

    private async Task WriteLogFileAsync()
    {
        await foreach (var entry in _logFile.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                var file = Path.Combine(DataDirectory, "log.txt");
                if (File.Exists(file) && new FileInfo(file).Length > LogFileLimit)
                {
                    File.Move(file, Path.Combine(DataDirectory, "log.1.txt"), overwrite: true);
                }
                await File.AppendAllTextAsync(file, $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} {entry.Level.ToString().ToUpperInvariant(),-5} {entry.Text}\n").ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The log file is a convenience; the in-memory log still has the line.
            }
        }
    }
}
