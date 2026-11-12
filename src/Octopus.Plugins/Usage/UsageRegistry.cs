using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Octopus.Plugins.Usage;

/// <summary>One agent account's usage as a provider last reported it (design/plugins/11-补充能力.md §2.2, 12 §5).</summary>
/// <param name="Snapshot">The provider's snapshot: <c>windows</c>, <c>credits</c>, <c>session</c>, <c>source</c>, <c>unavailable</c>…</param>
public sealed record UsageState(string AgentId, string? Account, string ProviderPlugin, JsonObject Snapshot, DateTimeOffset UpdatedAt)
{
    /// <summary>No update for 10 minutes: the app shows "updated N minutes ago".</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    public bool IsStale(DateTimeOffset now) => now - UpdatedAt > StaleAfter;
}

/// <summary>
/// The usage the app shows in the indicator, the launcher and agent settings. Providers (plugins) only report data;
/// the registry keeps the last snapshot per agent and account, survives restarts in <c>usage-cache.json</c>, and
/// blanks windows whose reset time has passed so an old percentage is never shown as current.
/// </summary>
public sealed class UsageRegistry
{
    public const int MaxSnapshotBytes = 16 * 1024;

    private readonly string _cacheFile;
    private readonly ConcurrentDictionary<(string Agent, string Account), UsageState> _states = new();
    private readonly ConcurrentDictionary<string, string> _providers = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _writeGate = new();

    public UsageRegistry(PluginPaths paths, Func<DateTimeOffset>? clock = null)
    {
        _cacheFile = Path.Combine(Path.GetDirectoryName(paths.PluginsDirectory)!, "usage-cache.json");
        _clock = clock ?? (() => DateTimeOffset.Now);
        Load();
    }

    /// <summary>(agent, account) whose usage changed.</summary>
    public event EventHandler<(string AgentId, string? Account)>? Changed;

    /// <summary>The plugin providing usage for an agent; the first registration wins until it unregisters.</summary>
    public bool TryClaim(string agentId, string pluginId) => _providers.GetOrAdd(agentId, pluginId) == pluginId;

    public void Release(string pluginId)
    {
        foreach (var (agent, owner) in _providers)
        {
            if (owner == pluginId) _providers.TryRemove(agent, out _);
        }
    }

    public string? ProviderOf(string agentId) => _providers.GetValueOrDefault(agentId);

    public void Update(string agentId, string pluginId, JsonObject snapshot)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(snapshot.ToJsonString()) > MaxSnapshotBytes)
        {
            throw new ArgumentException($"A usage snapshot is limited to {MaxSnapshotBytes / 1024} KB.");
        }
        var account = snapshot["account"]?.GetValue<string>();
        _states[(agentId, account ?? "")] = new UsageState(agentId, account, pluginId, snapshot, _clock());
        Save();
        Changed?.Invoke(this, (agentId, account));
    }

    public void Clear(string agentId, string? account)
    {
        var removed = account is null
            ? _states.Keys.Where(k => k.Agent == agentId).ToList().Count(k => _states.TryRemove(k, out _))
            : _states.TryRemove((agentId, account), out _) ? 1 : 0;
        if (removed == 0) return;
        Save();
        Changed?.Invoke(this, (agentId, account));
    }

    /// <summary>The usage of an agent: the given account, or the most recently updated one.</summary>
    public UsageState? Get(string agentId, string? account = null)
    {
        UsageState? state = account is not null
            ? _states.GetValueOrDefault((agentId, account))
            : _states.Values.Where(s => s.AgentId == agentId).MaxBy(s => s.UpdatedAt);
        return state is null ? null : WithResetWindows(state);
    }

    public IReadOnlyList<UsageState> All => _states.Values.Select(WithResetWindows).ToList();

    /// <summary>A window whose reset time has passed shows "reset" instead of its old percentage.</summary>
    private UsageState WithResetWindows(UsageState state)
    {
        if (state.Snapshot["windows"] is not JsonArray windows) return state;
        var nowMs = _clock().ToUnixTimeMilliseconds();
        var changed = false;
        var copy = (JsonObject)state.Snapshot.DeepClone();
        foreach (var window in ((JsonArray)copy["windows"]!).OfType<JsonObject>())
        {
            if (window["resetsAt"] is JsonValue reset && reset.TryGetValue<double>(out var at) && at <= nowMs && window["usedPercent"] is not null)
            {
                window.Remove("usedPercent");
                window["reset"] = true;
                changed = true;
            }
        }
        return changed ? state with { Snapshot = copy } : state;
    }

    private void Save()
    {
        var all = new JsonArray();
        foreach (var state in _states.Values)
        {
            all.Add((JsonNode)new JsonObject
            {
                ["agent"] = state.AgentId,
                ["account"] = state.Account,
                ["provider"] = state.ProviderPlugin,
                ["updatedAt"] = state.UpdatedAt.ToUnixTimeMilliseconds(),
                ["snapshot"] = state.Snapshot.DeepClone(),
            });
        }
        lock (_writeGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
                var temp = $"{_cacheFile}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temp, all.ToJsonString());
                File.Move(temp, _cacheFile, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_cacheFile) || JsonNode.Parse(File.ReadAllText(_cacheFile)) is not JsonArray all) return;
            foreach (var item in all.OfType<JsonObject>())
            {
                if (item["agent"]?.GetValue<string>() is not { } agent || item["snapshot"] is not JsonObject snapshot) continue;
                var account = item["account"]?.GetValue<string>();
                var updated = DateTimeOffset.FromUnixTimeMilliseconds((long)(item["updatedAt"]?.GetValue<double>() ?? 0));
                _states[(agent, account ?? "")] = new UsageState(agent, account, item["provider"]?.GetValue<string>() ?? "", (JsonObject)snapshot.DeepClone(), updated);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
        }
    }
}
