using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Octopus.Plugins.Runtime.Modules;

/// <summary><c>octopus.devices</c>: active device context for everyone; full device catalog with <c>devices: read</c>.</summary>
public sealed class DevicesModule : IPluginApiModule
{
    private readonly IPluginDeviceContext _devices;
    private readonly ConcurrentDictionary<PluginHost, byte> _currentListeners = new();
    private readonly ConcurrentDictionary<PluginHost, byte> _listListeners = new();

    public DevicesModule(IPluginDeviceContext devices)
    {
        _devices = devices;
        devices.CurrentChanged += (_, _) =>
        {
            var payload = Json(devices.Current);
            foreach (var plugin in _currentListeners.Keys) plugin.Emit("devices.current", payload.DeepClone());
        };
        devices.ListChanged += (_, _) =>
        {
            var payload = JsonList(devices.All);
            foreach (var plugin in _listListeners.Keys)
            {
                if (plugin.Grants.Has("devices")) plugin.Emit("devices.list", payload.DeepClone());
            }
        };
    }

    public static JsonObject Json(PluginDeviceInfo device)
    {
        var caps = new JsonArray();
        foreach (var c in device.Capabilities) caps.Add((JsonNode?)JsonValue.Create(c));
        return new JsonObject
        {
            ["key"] = device.Key,
            ["kind"] = device.Kind,
            ["name"] = device.Name,
            ["target"] = device.Target,
            ["status"] = device.Status,
            ["capabilities"] = caps,
        };
    }

    private static JsonArray JsonList(IReadOnlyList<PluginDeviceInfo> devices)
    {
        var arr = new JsonArray();
        foreach (var d in devices) arr.Add((JsonNode)Json(d));
        return arr;
    }

    public void Register(PluginApiRouter router)
    {
        router.Sync("devices.current", _ => Json(_devices.Current).ToJsonString());
        router.Request("devices.list", (call, _) =>
        {
            call.Plugin.Grants.Require("devices");
            return Task.FromResult(PluginReply.Of(JsonList(_devices.All)));
        });
    }

    public void Subscribed(PluginHost plugin, PluginSubscription subscription)
    {
        if (subscription.Event == "devices.current") _currentListeners[plugin] = 0;
        else if (subscription.Event == "devices.list") _listListeners[plugin] = 0;
    }

    public void Reset(PluginHost plugin)
    {
        _currentListeners.TryRemove(plugin, out _);
        _listListeners.TryRemove(plugin, out _);
    }
}
