using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Octopus.Core.Plugins;
using Octopus.Plugins.Catalog;
using Octopus.Storage;

namespace Octopus.Plugins.Runtime;

/// <summary>Restart and budget policy (design/plugins/02-运行时.md §5–6). Tests shorten the delays.</summary>
public sealed class PluginManagerOptions
{
    public TurnBudgets Budgets { get; init; } = TurnBudgets.Default;
    /// <summary>Delay before the first, second and later automatic restarts after a crash.</summary>
    public IReadOnlyList<TimeSpan> RestartDelays { get; init; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];
    /// <summary>This many crashes within <see cref="CrashWindow"/> stop the plugin until the user starts it again.</summary>
    public int MaxCrashes { get; init; } = 3;
    public TimeSpan CrashWindow { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>Asks the user for a permission a plugin requests at run time. The default refuses; the app shows a dialog.</summary>
public interface IPluginConsent
{
    Task<bool> RequestPermissionAsync(PluginHost plugin, PermissionRequest request, string? reason, CancellationToken cancellationToken);
}

public sealed class DenyingPluginConsent : IPluginConsent
{
    public Task<bool> RequestPermissionAsync(PluginHost plugin, PermissionRequest request, string? reason, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

/// <summary>Runs Octopus's own commands (<c>octopus.*</c>) for plugins; set by the app.</summary>
public interface IAppCommandExecutor
{
    bool CanExecute(string commandId);
    Task<JsonNode?> ExecuteAsync(string commandId, JsonArray args, CancellationToken cancellationToken);
}

/// <summary>
/// All plugins at run time: one <see cref="PluginHost"/> per enabled plugin with code, activated on its activation
/// events, restarted after a crash with back-off and stopped after <see cref="PluginManagerOptions.MaxCrashes"/>.
/// </summary>
public sealed class PluginManager : IPluginHostOwner, IAsyncDisposable, IDisposable
{
    private readonly IPluginCatalog _catalog;
    private readonly IPluginStateStore _state;
    private readonly PluginManagerOptions _options;
    private readonly IPluginConsent _consent;
    private readonly ConcurrentDictionary<string, PluginHost> _hosts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<DateTimeOffset>> _crashes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private string? _currentProjectPath;

    public PluginManager(
        IPluginCatalog catalog,
        IPluginStateStore state,
        IPluginRuntimeFactory runtimeFactory,
        IEnumerable<IPluginApiModule> modules,
        PluginPaths paths,
        PluginManagerOptions? options = null,
        IPluginConsent? consent = null)
    {
        _catalog = catalog;
        _state = state;
        _options = options ?? new PluginManagerOptions();
        _consent = consent ?? new DenyingPluginConsent();
        RuntimeFactory = runtimeFactory;
        Paths = paths;
        Router = new PluginApiRouter([new ManagerModule(this), .. modules]);
        _state.Changed += (_, _) => RefreshGrants();
    }

    public PluginApiRouter Router { get; }
    public IPluginRuntimeFactory RuntimeFactory { get; }
    public PluginPaths Paths { get; }
    public TurnBudgets Budgets => _options.Budgets;

    /// <summary>Runs <c>octopus.*</c> commands for plugins; null until the app sets it.</summary>
    public IAppCommandExecutor? AppCommands { get; set; }

    /// <summary>Opens a plugin view tab for its <c>&lt;view id&gt;.open</c> command; set by the app.</summary>
    public Func<PluginHost, ViewContribution, JsonArray, Task>? ViewOpener { get; set; }

    public IReadOnlyCollection<PluginHost> Hosts => _hosts.Values.ToList();

    /// <summary>A host was added or removed, or changed state.</summary>
    public event EventHandler<PluginHost>? HostChanged;

    /// <summary>A plugin registered or dropped commands or providers.</summary>
    public event EventHandler<PluginHost>? RegistrationsChanged;

    public PluginHost? Find(string id) => _hosts.GetValueOrDefault(id);

    // ---------------------------------------------------------------- start and stop

    /// <summary>Reads the catalog, creates the hosts of enabled plugins and activates those that ask for <c>onStartup</c>.</summary>
    public Task StartAsync()
    {
        _catalog.Refresh();
        SyncHosts();
        var startup = _hosts.Values.Where(h => h.Manifest.Activation.Contains("onStartup")).ToList();
        return Task.WhenAll(startup.Select(h => ActivateQuietlyAsync(h)));
    }

    /// <summary>Creates hosts for newly enabled plugins and removes hosts of plugins that are gone or disabled.</summary>
    public void SyncHosts()
    {
        var wanted = _catalog.Packages
            .Where(p => p.Status == PluginStatus.Enabled && p.Manifest?.Main is not null)
            .ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var (id, host) in _hosts)
        {
            if (!wanted.TryGetValue(id, out var package) || package.Directory != host.Package.Directory || package.Manifest!.Version != host.Manifest.Version)
            {
                if (_hosts.TryRemove(id, out _))
                {
                    _ = RetireAsync(host);
                }
            }
        }
        foreach (var (id, package) in wanted)
        {
            if (_hosts.ContainsKey(id)) continue;
            var host = new PluginHost(package, GrantsFor(package), this);
            if (_hosts.TryAdd(id, host)) HostChanged?.Invoke(this, host);
        }
    }

    private async Task RetireAsync(PluginHost host)
    {
        await host.DeactivateAsync(PluginHostState.Stopped).ConfigureAwait(false);
        await host.DisposeAsync().ConfigureAwait(false);
        HostChanged?.Invoke(this, host);
    }

    /// <summary>Switches a plugin on or off and saves the choice.</summary>
    public async Task SetEnabledAsync(string id, bool enabled)
    {
        var package = _catalog.Find(id);
        _state.Update(d =>
        {
            var record = d.Find(id) ?? (d.Plugins[id] = new PluginRecord { Source = package?.Source ?? PluginSource.File, Version = package?.Manifest?.Version, InstalledAt = DateTimeOffset.Now });
            record.Enabled = enabled;
        });
        _crashes.TryRemove(id, out _);
        _catalog.Refresh();
        SyncHosts();
        if (enabled && _hosts.TryGetValue(id, out var host) && host.Manifest.Activation.Contains("onStartup"))
        {
            await ActivateQuietlyAsync(host).ConfigureAwait(false);
        }
    }

    /// <summary>Stops and starts a plugin again (also after it was stopped for crashing too often).</summary>
    public async Task ReloadAsync(string id)
    {
        _crashes.TryRemove(id, out _);
        if (_hosts.TryRemove(id, out var host))
        {
            await RetireAsync(host).ConfigureAwait(false);
        }
        _catalog.Refresh();
        SyncHosts();
        if (_hosts.TryGetValue(id, out var fresh))
        {
            await ActivateQuietlyAsync(fresh).ConfigureAwait(false);
        }
    }

    /// <summary>For containers that dispose synchronously (the app's, at exit): waits up to 3 seconds off the caller's context.</summary>
    public void Dispose() => Task.Run(() => DisposeAsync().AsTask()).GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_shutdown.IsCancellationRequested) return;
        _shutdown.Cancel();
        var hosts = _hosts.Values.ToList();
        _hosts.Clear();
        var all = Task.WhenAll(hosts.Select(async h =>
        {
            await h.DeactivateAsync(PluginHostState.Stopped).ConfigureAwait(false);
            await h.DisposeAsync().ConfigureAwait(false);
        }));
        await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- activation

    /// <summary>Activates a plugin if needed; returns its host once running, null when it cannot run.</summary>
    public async Task<PluginHost?> EnsureActiveAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!_hosts.TryGetValue(id, out var host)) return null;
        if (host.State is PluginHostState.Stopped or PluginHostState.Failed) return null;
        if (host.State != PluginHostState.Running)
        {
            await host.ActivateAsync(cancellationToken).ConfigureAwait(false);
        }
        return host.State == PluginHostState.Running ? host : null;
    }

    /// <summary>
    /// An activation event happened (design/plugins/03-清单与生命周期.md §4.2): <c>onProject</c>, <c>onTerminal</c>,
    /// <c>onAgent:&lt;id&gt;</c>, <c>onAgentInstalled:&lt;id&gt;</c>, <c>onFile:&lt;path&gt;</c>.
    /// </summary>
    public Task FireAsync(string activationEvent)
    {
        var hosts = _hosts.Values.Where(h => h.State == PluginHostState.Inactive && Matches(h.Manifest.Activation, activationEvent)).ToList();
        return Task.WhenAll(hosts.Select(h => ActivateQuietlyAsync(h)));
    }

    /// <summary>The current project changed: fires <c>onProject</c> and the <c>onProject:contains:</c> events that match it.</summary>
    public Task ProjectOpenedAsync(string? projectPath)
    {
        _currentProjectPath = projectPath;
        if (projectPath is null) return Task.CompletedTask;
        var hosts = _hosts.Values.Where(h => h.State == PluginHostState.Inactive && h.Manifest.Activation.Any(a =>
            a == "onProject" || (a.StartsWith("onProject:contains:", StringComparison.Ordinal) && ProjectContains(projectPath, a["onProject:contains:".Length..])))).ToList();
        return Task.WhenAll(hosts.Select(h => ActivateQuietlyAsync(h)));
    }

    private static bool Matches(IReadOnlyList<string> activation, string fired) =>
        activation.Any(a => a == fired || (a.StartsWith("onFile:", StringComparison.Ordinal) && fired.StartsWith("onFile:", StringComparison.Ordinal)
            && Glob.IsMatch(a["onFile:".Length..], Path.GetFileName(fired["onFile:".Length..]))));

    /// <summary>Looks at the project root and its direct sub-directories only (an activation check must stay cheap).</summary>
    private static bool ProjectContains(string root, string glob)
    {
        try
        {
            if (Directory.EnumerateFileSystemEntries(root).Any(e => Glob.IsMatch(glob, Path.GetFileName(e)))) return true;
            return Directory.EnumerateDirectories(root).Take(200)
                .Any(d => Directory.EnumerateFileSystemEntries(d).Any(e => Glob.IsMatch(glob, $"{Path.GetFileName(d)}/{Path.GetFileName(e)}")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ActivateQuietlyAsync(PluginHost host)
    {
        try
        {
            await host.ActivateAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---------------------------------------------------------------- commands

    /// <summary>Commands contributed by enabled plugins (declared in their manifests; available before activation).</summary>
    public IEnumerable<(PluginHost Host, CommandContribution Command)> Commands =>
        _hosts.Values.SelectMany(h => h.Manifest.Contributes.Commands.Select(c => (h, c)));

    /// <summary>Runs a plugin command, activating its plugin first; or an Octopus command when the id starts with <c>octopus.</c>.</summary>
    public async Task<JsonNode?> ExecuteCommandAsync(string commandId, JsonArray? args = null, CancellationToken cancellationToken = default)
    {
        args ??= [];
        // Built-in plugins have ids under "octopus." too, so a declared plugin command is looked for first.
        var owner = _hosts.Values.FirstOrDefault(h =>
            h.Manifest.Contributes.Commands.Any(c => c.Id == commandId) || h.Manifest.Contributes.Views.Any(v => $"{v.Id}.open" == commandId));
        if (owner is null)
        {
            if (AppCommands is { } app && app.CanExecute(commandId)) return await app.ExecuteAsync(commandId, args, cancellationToken).ConfigureAwait(false);
            throw new PluginApiException("NotFound", $"No enabled plugin provides the command \"{commandId}\".");
        }
        // "<view id>.open" is generated for every view (design/plugins/06-标签与渲染.md §3): the app opens the tab,
        // and the tab activates the plugin when it is shown.
        if (owner.Manifest.Contributes.Views.FirstOrDefault(v => $"{v.Id}.open" == commandId) is { } view
            && !owner.Manifest.Contributes.Commands.Any(c => c.Id == commandId))
        {
            if (ViewOpener is null) throw PluginApiException.Unsupported("Opening plugin views");
            await ViewOpener(owner, view, args).ConfigureAwait(false);
            return null;
        }
        var host = await EnsureActiveAsync(owner.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new PluginApiException("Unavailable", $"The plugin {owner.Id} is not running: {owner.LastError}");
        if (!host.Commands.Contains(commandId))
        {
            throw new PluginApiException("NotFound", $"{host.Id} declares \"{commandId}\" but did not register a handler for it in activate().");
        }
        var result = await host.InvokeAsync($"command:{commandId}", args, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(result);
    }

    // ---------------------------------------------------------------- events to every plugin

    /// <summary>Sends an event to every running plugin that subscribed to it.</summary>
    public void Broadcast(string @event, JsonNode? payload, Func<JsonNode?, bool>? matches = null)
    {
        foreach (var host in _hosts.Values)
        {
            if (host.State == PluginHostState.Running) host.Emit(@event, payload, matches);
        }
    }

    // ---------------------------------------------------------------- grants

    private PluginGrants GrantsFor(PluginPackage package) =>
        new(package.Manifest!, _state.Current.Find(package.Id), package.Source == PluginSource.Builtin);

    private void RefreshGrants()
    {
        foreach (var host in _hosts.Values)
        {
            host.Grants = GrantsFor(host.Package);
            if (host.State == PluginHostState.Running) host.Emit("permissions.changed", null);
        }
    }

    // ---------------------------------------------------------------- IPluginHostOwner

    void IPluginHostOwner.OnFault(PluginHost host, PluginFault fault) => _ = HandleCrashAsync(host, fault);

    private async Task HandleCrashAsync(PluginHost host, PluginFault fault)
    {
        await host.CrashAsync(fault.Message).ConfigureAwait(false);
        var now = DateTimeOffset.Now;
        var recent = _crashes.GetOrAdd(host.Id, _ => []);
        int count;
        lock (recent)
        {
            recent.Add(now);
            recent.RemoveAll(t => now - t > _options.CrashWindow);
            count = recent.Count;
        }
        _state.Update(d =>
        {
            if (d.Find(host.Id) is { } record) record.Crashes.Add(now);
        });
        if (count >= _options.MaxCrashes)
        {
            host.MarkStopped($"Stopped after {count} crashes within {_options.CrashWindow.TotalMinutes:0} minutes. Last: {fault.Message}");
            return;
        }
        var delay = _options.RestartDelays[Math.Min(count - 1, _options.RestartDelays.Count - 1)];
        try
        {
            await Task.Delay(delay, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_hosts.TryGetValue(host.Id, out var current) && current == host && host.State == PluginHostState.Crashed)
        {
            await ActivateQuietlyAsync(host).ConfigureAwait(false);
        }
    }

    void IPluginHostOwner.OnRegistrationsChanged(PluginHost host) => RegistrationsChanged?.Invoke(this, host);

    void IPluginHostOwner.OnStateChanged(PluginHost host) => HostChanged?.Invoke(this, host);

    // ---------------------------------------------------------------- the manager's own API

    private sealed class ManagerModule(PluginManager manager) : IPluginApiModule
    {
        public void Register(PluginApiRouter router)
        {
            router.Request("commands.execute", async (call, ct) =>
            {
                var id = call.RequiredString("id");
                var args = call["args"] as JsonArray ?? [];
                var result = await manager.ExecuteCommandAsync(id, (JsonArray)args.DeepClone(), ct).ConfigureAwait(false);
                return PluginReply.Of(result);
            });
            router.Request("commands.list", (call, _) =>
            {
                var list = new JsonArray();
                foreach (var (host, command) in manager.Commands)
                {
                    list.Add((JsonNode)new JsonObject { ["id"] = command.Id, ["title"] = command.Title, ["category"] = command.Category, ["plugin"] = host.Id });
                }
                return Task.FromResult(PluginReply.Of(list));
            });
            router.Sync("permissions.has", call => call.Plugin.Grants.Has(call.RequiredString("permission")) ? "true" : "false");
            router.Request("permissions.request", async (call, ct) =>
            {
                var key = call.RequiredString("permission");
                var grants = call.Plugin.Grants;
                if (grants.Has(key)) return PluginReply.Of(true);
                var request = grants.Manifest.Permissions.Requests.FirstOrDefault(r => r.Key == key);
                if (request is null || (grants.Record?.Denied.Contains(key) ?? false)) return PluginReply.Of(false);
                var granted = await manager._consent.RequestPermissionAsync(call.Plugin, request, call.String("reason"), ct).ConfigureAwait(false);
                manager._state.Update(d =>
                {
                    var record = d.Find(call.Plugin.Id) ?? (d.Plugins[call.Plugin.Id] = new PluginRecord { Enabled = true, Source = call.Plugin.Package.Source, Version = call.Plugin.Manifest.Version });
                    if (granted) record.Grants[key] = request.Details.FirstOrDefault() ?? "granted";
                    else if (!record.Denied.Contains(key)) record.Denied.Add(key);
                });
                return PluginReply.Of(granted);
            });
        }
    }
}
