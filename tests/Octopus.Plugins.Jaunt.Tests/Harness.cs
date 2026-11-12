using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Octopus.Core.Plugins;
using Octopus.Platform;
using Octopus.Plugins.Catalog;
using Octopus.Plugins.Runtime;
using Octopus.Plugins.Runtime.Modules;
using Octopus.Plugins.Usage;
using Octopus.Storage;

namespace Octopus.Plugins.Jaunt.Tests;

/// <summary>A real plugin manager on real Jaunt engines, over a temporary data directory.</summary>
public sealed class Harness : IAsyncDisposable, IAppPaths
{
    private readonly string _root = Directory.CreateTempSubdirectory("octopus-runtime-").FullName;
    private PluginManager? _manager;

    public string DataDirectory => Path.Combine(_root, "data");
    public string CacheDirectory => Path.Combine(_root, "cache");
    public string BuiltinRoot => Path.Combine(_root, "builtin");

    public PluginPaths Paths => new(this);
    public InMemorySettingsStore Settings { get; } = new();
    public MemoryState State { get; } = new();
    public CapturingModule Captured { get; } = new();
    public UsageRegistry Usage => _usage ??= new UsageRegistry(Paths);
    private UsageRegistry? _usage;
    public PluginConfigStore Config => _config ??= new PluginConfigStore(Paths);
    private PluginConfigStore? _config;
    public IPluginConsent? Consent { get; set; }

    /// <summary>Runs user commands for <c>config.runCommand</c>; null leaves it unsupported.</summary>
    public UserCommandRunner? Commands { get; set; }

    /// <summary>More host modules for a test (fakes of the app's).</summary>
    public List<IPluginApiModule> ExtraModules { get; } = [];

    public PluginManagerOptions Options { get; set; } = new()
    {
        RestartDelays = [TimeSpan.FromMilliseconds(50)],
        MaxCrashes = 3,
        Budgets = TurnBudgets.Default with { Normal = TimeSpan.FromMilliseconds(500), Activate = TimeSpan.FromSeconds(5) },
    };

    /// <summary>Writes a built-in plugin package (built-in: enabled and granted what it declares).</summary>
    public string Plugin(string id, string main, string? manifestExtra = null, params (string Path, string Content)[] files)
    {
        var directory = Path.Combine(BuiltinRoot, id);
        Directory.CreateDirectory(directory);
        var extra = manifestExtra is null ? "" : ", " + manifestExtra;
        File.WriteAllText(Path.Combine(directory, "plugin.json"),
            $$"""{ "schema": 1, "id": "{{id}}", "name": "{{id}}", "version": "1.0.0", "engines": { "api": 0 }, "main": "main.js"{{extra}} }""");
        File.WriteAllText(Path.Combine(directory, "main.js"), main);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return directory;
    }

    /// <summary>Writes an installed (not built-in) plugin, enabled but holding only <paramref name="grants"/>.</summary>
    public string InstalledPlugin(string id, string main, string? manifestExtra, IReadOnlyDictionary<string, string> grants)
    {
        var directory = Plugin(id, main, manifestExtra);
        var target = Paths.PackageDirectoryOf(id);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(directory, target);
        State.Update(d => d.Plugins[id] = new PluginRecord { Enabled = true, Source = PluginSource.File, Version = "1.0.0", Grants = new(grants) });
        return target;
    }

    public async Task<PluginManager> StartAsync()
    {
        SemanticVersion.TryParse("1.0.0", out var version);
        var host = PluginHostInfo.Current(version);
        var catalog = new PluginCatalog(Paths, State, host, new PluginCatalogOptions { BuiltinRoots = [BuiltinRoot] });
        IPluginApiModule[] modules =
        [
            new EnvModule(host, Settings), new StorageModule(), new ConfigModule(Config, Commands), new UsageModule(Usage), Captured,
            .. ExtraModules,
        ];
        _manager = new PluginManager(catalog, State, new JauntPluginRuntimeFactory(), modules, Paths, Options, Consent);
        await _manager.StartAsync();
        return _manager;
    }

    public static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("The condition did not become true in time.");
            await Task.Delay(10);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_manager is not null) await _manager.DisposeAsync();
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class MemoryState : IPluginStateStore
{
    public PluginsDocument Current { get; } = new();
    public event EventHandler? Changed;
    public void Load() => Changed?.Invoke(this, EventArgs.Empty);
    public void Update(Action<PluginsDocument> mutate)
    {
        lock (Current) mutate(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Stands in for the app's modules: records view updates and answers a test request.</summary>
public sealed class CapturingModule : IPluginApiModule
{
    public ConcurrentQueue<(string Plugin, string Method, JsonNode? Args)> Notifications { get; } = new();

    public void Register(PluginApiRouter router)
    {
        foreach (var method in new[] { "view.update", "view.chrome", "ui.toast" })
        {
            router.Notify(method, call => Notifications.Enqueue((call.Plugin.Id, call.Method, call.Args?.DeepClone())));
        }
        router.Request("test.echo", (call, _) => Task.FromResult(new PluginReply(call.Json, call.Text)));
        router.Request("test.secret", (call, _) =>
        {
            call.Plugin.Grants.Require("git");
            return Task.FromResult(PluginReply.Of("ok"));
        });
    }
}

public sealed class FixedConsent(bool answer) : IPluginConsent
{
    public List<string> Asked { get; } = [];

    public Task<bool> RequestPermissionAsync(PluginHost plugin, PermissionRequest request, string? reason, CancellationToken cancellationToken)
    {
        Asked.Add($"{request.Key}:{reason}");
        return Task.FromResult(answer);
    }
}
