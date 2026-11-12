using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Octopus.Plugins.Usage;

namespace Octopus.Plugins.Runtime.Modules;

/// <summary>
/// <c>agents.registerUsageProvider</c> and <c>agents.usage</c>: plugins report usage, the host shows it
/// (design/plugins/11-补充能力.md §2). A plugin may only provide for agents its manifest lists in <c>usageProviders</c>.
/// </summary>
public sealed class UsageModule : IPluginApiModule
{
    private readonly UsageRegistry _registry;
    private readonly ConcurrentDictionary<PluginHost, byte> _listeners = new();

    public UsageModule(UsageRegistry registry)
    {
        _registry = registry;
        registry.Changed += (_, change) =>
        {
            var payload = new JsonObject { ["agentId"] = change.AgentId, ["account"] = change.Account };
            foreach (var plugin in _listeners.Keys) plugin.Emit("usage.changed", payload);
        };
    }

    public void Register(PluginApiRouter router)
    {
        router.Notify("usage.register", call =>
        {
            var agent = DeclaredAgent(call);
            if (!_registry.TryClaim(agent, call.Plugin.Id))
            {
                call.Plugin.Write(Runtime.PluginLogLevel.Warn, $"Usage for {agent} is already provided by {_registry.ProviderOf(agent)}; this plugin's reports are ignored.");
            }
        });
        router.Notify("usage.update", call =>
        {
            var agent = DeclaredAgent(call);
            if (_registry.ProviderOf(agent) != call.Plugin.Id) return;
            if (call["snapshot"] is not JsonObject snapshot) throw PluginApiException.Invalid("The snapshot must be an object.");
            _registry.Update(agent, call.Plugin.Id, (JsonObject)snapshot.DeepClone());
        });
        router.Notify("usage.clear", call =>
        {
            var agent = DeclaredAgent(call);
            if (_registry.ProviderOf(agent) == call.Plugin.Id) _registry.Clear(agent, call.String("account"));
        });
        router.Notify("usage.unregister", call => _registry.Release(call.Plugin.Id));
        // What the host remembers of every account of an agent (it survives restarts): a plugin starts from it instead
        // of asking again. Each item is { account, updatedAt, snapshot }; windows past their reset carry no percentage.
        router.Sync("usage.list", call =>
        {
            var agent = call.RequiredString("agentId");
            var list = new JsonArray();
            foreach (var state in _registry.All.Where(s => s.AgentId == agent).OrderBy(s => s.Account, StringComparer.Ordinal))
            {
                list.Add((JsonNode)new JsonObject
                {
                    ["account"] = state.Account ?? "",
                    ["updatedAt"] = state.UpdatedAt.ToUnixTimeMilliseconds(),
                    ["snapshot"] = state.Snapshot.DeepClone(),
                });
            }
            return list.ToJsonString();
        });
        router.Sync("usage.get", call =>
        {
            var state = _registry.Get(call.RequiredString("agentId"), call.String("account"));
            if (state is null) return "";
            var result = (JsonObject)state.Snapshot.DeepClone();
            result["updatedAt"] = state.UpdatedAt.ToUnixTimeMilliseconds();
            result["stale"] = state.IsStale(DateTimeOffset.Now);
            return result.ToJsonString();
        });
    }

    public void Subscribed(PluginHost plugin, PluginSubscription subscription)
    {
        if (subscription.Event == "usage.changed") _listeners[plugin] = 0;
    }

    public void Reset(PluginHost plugin)
    {
        _listeners.TryRemove(plugin, out _);
        _registry.Release(plugin.Id);
    }

    private static string DeclaredAgent(PluginCall call)
    {
        var agent = call.RequiredString("agentId");
        if (!call.Plugin.Manifest.Contributes.UsageProviders.Any(p => p.Agent == agent))
        {
            throw PluginApiException.Invalid($"{call.Plugin.Id} does not declare a usage provider for {agent} in contributes.usageProviders.");
        }
        return agent;
    }
}
