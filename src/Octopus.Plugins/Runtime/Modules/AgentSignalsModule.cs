using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Agents;
using Octopus.Agents.Probes;
using Octopus.Agents.Signals;
using Octopus.Platform;

namespace Octopus.Plugins.Runtime.Modules;

/// <summary>
/// <c>agents.onSignal</c>, <c>agents.lastSignal</c>, <c>agents.accounts</c> and <c>agents.runProbe</c>
/// (design/plugins/11-补充能力.md §1–§1A). Each needs the matching grant: <c>agents.signals.&lt;agent&gt;.&lt;channel&gt;</c>
/// or <c>agents.probes.&lt;agent&gt;.&lt;probe&gt;</c>. Account directories never leave the host: plugins see a key and a label.
/// </summary>
public sealed class AgentSignalsModule(
    ISignalHub hub,
    IProbeRunner probes,
    IAgentCatalog catalog,
    IAgentScanner scanner,
    ILoginEnvironment login,
    PluginHostInfo host,
    Octopus.Storage.ISettingsStore? settings = null,
    IShellCommandBuilder? shell = null,
    IAgentAccountDiscovery? discovery = null) : IPluginApiModule
{
    private readonly ConcurrentDictionary<(string Plugin, string Key), IDisposable> _subscriptions = new();

    public void Register(PluginApiRouter router)
    {
        router.Sync("agents.lastSignal", call =>
        {
            var agent = call.RequiredString("agentId");
            var channel = call.RequiredString("channel");
            call.Plugin.Grants.Require($"agents.signals.{agent}.{channel}");
            return hub.LastSignal(agent, channel, call.String("itemId")) is { } signal ? Describe(signal).ToJsonString() : "null";
        });
        router.Sync("agents.accounts", call =>
        {
            var agent = call.RequiredString("agentId");
            if (!HoldsAny(call.Plugin, agent)) throw PluginApiException.Denied($"agents.signals.{agent}", canRequest: false);
            return Describe(hub.Accounts(agent)).ToJsonString();
        });
        // Every account on this machine, found by the host: usage is asked of all of them, not only of those that
        // already reported. Falls back to the accounts the hub knows when there is no discovery.
        router.Request("agents.discoverAccounts", async (call, cancellationToken) =>
        {
            var agent = call.RequiredString("agentId");
            if (!HoldsAny(call.Plugin, agent)) throw PluginApiException.Denied($"agents.signals.{agent}", canRequest: false);
            var accounts = discovery is null ? hub.Accounts(agent) : await discovery.DiscoverAsync(agent, cancellationToken).ConfigureAwait(false);
            return PluginReply.Of(Describe(accounts));
        });
        router.Request("agents.runProbe", RunProbeAsync);
    }

    private static JsonArray Describe(IEnumerable<AgentAccount> accounts)
    {
        var list = new JsonArray();
        foreach (var account in accounts)
        {
            list.Add((JsonNode)new JsonObject { ["key"] = account.Key, ["label"] = account.DefaultLabel });
        }
        return list;
    }

    private static bool HoldsAny(PluginHost plugin, string agent) =>
        plugin.Manifest.Permissions.Requests.Any(r =>
            (r.Key.StartsWith($"agents.signals.{agent}.", StringComparison.Ordinal) || r.Key.StartsWith($"agents.probes.{agent}.", StringComparison.Ordinal))
            && plugin.Grants.Has(r.Key));

    public void Subscribed(PluginHost plugin, PluginSubscription subscription)
    {
        if (subscription.Event != "agents.signal") return;
        var agent = subscription.Args?["agentId"]?.GetValue<string>();
        var channel = subscription.Args?["channel"]?.GetValue<string>();
        if (agent is null || channel is null)
        {
            plugin.Write(PluginLogLevel.Warn, "agents.onSignal needs an agent id and a channel.");
            return;
        }
        if (!plugin.Grants.Has($"agents.signals.{agent}.{channel}"))
        {
            plugin.Write(PluginLogLevel.Warn, $"agents.onSignal('{agent}', '{channel}'): the permission agents.signals.{agent}.{channel} is not granted; no signals are delivered.");
            return;
        }
        // Every signal is checked against the current grant again, so revoking it stops delivery at once.
        var registration = hub.Subscribe(agent, channel, signal =>
        {
            if (plugin.Grants.Has($"agents.signals.{agent}.{channel}")) plugin.EmitTo(subscription.Key, Describe(signal));
        });
        if (_subscriptions.TryRemove((plugin.Id, subscription.Key), out var old)) old.Dispose();
        _subscriptions[(plugin.Id, subscription.Key)] = registration;
    }

    public void Unsubscribed(PluginHost plugin, PluginSubscription subscription)
    {
        if (_subscriptions.TryRemove((plugin.Id, subscription.Key), out var registration)) registration.Dispose();
    }

    public void Reset(PluginHost plugin)
    {
        foreach (var key in _subscriptions.Keys.Where(k => k.Plugin == plugin.Id).ToList())
        {
            if (_subscriptions.TryRemove(key, out var registration)) registration.Dispose();
        }
    }

    /// <summary>The signal as plugins see it: the payload parsed when it is JSON, otherwise as text.</summary>
    internal static JsonObject Describe(AgentSignal signal) => new()
    {
        ["agentId"] = signal.AgentId,
        ["channel"] = signal.Channel,
        ["itemId"] = signal.ItemId.Length == 0 ? null : signal.ItemId,
        ["account"] = signal.Account,
        ["sentAt"] = signal.SentAt,
        ["receivedAt"] = signal.ReceivedAt,
        ["payload"] = ParseOrText(signal.Payload),
    };

    private static JsonNode? ParseOrText(string payload)
    {
        if (payload.Length == 0) return null;
        try
        {
            return JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            return JsonValue.Create(payload);
        }
    }

    private async Task<PluginReply> RunProbeAsync(PluginCall call, CancellationToken cancellationToken)
    {
        var agent = call.RequiredString("agentId");
        var probe = call.RequiredString("probe");
        call.Plugin.Grants.Require($"agents.probes.{agent}.{probe}");
        var definition = catalog.Find(agent) ?? throw new PluginApiException("NotFound", $"No agent \"{agent}\".");
        var installation = scanner.Find(agent) ?? throw new PluginApiException("NotFound", $"{agent} is not installed.");
        string? accountDirectory = null;
        if (call.String("account") is { Length: > 0 } key)
        {
            accountDirectory = hub.Accounts(agent).FirstOrDefault(a => a.Key == key)?.Directory
                ?? throw new PluginApiException("NotFound", $"No account \"{key}\" of {agent} is known.");
        }
        Dictionary<string, string>? arguments = null;
        if (call["args"] is JsonObject args)
        {
            arguments = [];
            foreach (var (name, value) in args)
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var text)) arguments[name] = text;
            }
        }
        var environment = await login.GetAsync(cancellationToken).ConfigureAwait(false);
        // The command the user set for the agent (a path, or a wrapper with arguments) runs the probe too.
        var executable = installation.Path;
        IReadOnlyList<string>? prefix = null;
        if (settings?.Current.Agents.ProfileOf(agent).Command is { Length: > 0 } custom && shell is not null)
        {
            var parts = shell.Split(custom);
            if (parts.Count > 0)
            {
                executable = parts[0];
                prefix = [.. parts.Skip(1)];
            }
        }
        var result = await probes.RunAsync(new ProbeRequest(
            definition, probe, executable, environment, accountDirectory, arguments,
            AppVersion: $"{host.AppVersion.Major}.{host.AppVersion.Minor}.{host.AppVersion.Patch}",
            PrefixArguments: prefix), cancellationToken).ConfigureAwait(false);
        return PluginReply.Of(new JsonObject
        {
            ["ok"] = result.Success,
            ["error"] = result.Success ? null : result.Error.ToString(),
            ["message"] = result.Message,
            ["account"] = result.Account,
            ["data"] = result.Json is null ? null : ParseOrText(result.Json),
            ["completedAt"] = result.CompletedAt.ToUnixTimeMilliseconds(),
            ["elapsedMs"] = (long)result.Elapsed.TotalMilliseconds,
            ["fromCache"] = result.FromCache,
            ["retryAt"] = result.RetryAt?.ToUnixTimeMilliseconds(),
        });
    }
}
