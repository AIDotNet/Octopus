using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Octopus.Plugins.Runtime.Modules;

/// <summary>
/// <c>ctx.globalState</c> and <c>ctx.projectState(id)</c>: small JSON key-value stores in the plugin's data directory
/// (<c>state.json</c>, <c>projects/&lt;project&gt;.json</c>), at most 1 MB each, written atomically on every change.
/// </summary>
public sealed class StorageModule : IPluginApiModule
{
    public const int MaxStoreBytes = 1024 * 1024;

    private readonly ConcurrentDictionary<string, JsonObject> _stores = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public void Register(PluginApiRouter router)
    {
        router.Request("storage.get", (call, _) => WithStore(call, store => PluginReply.Of(store[call.RequiredString("key")]?.DeepClone()), write: false));
        router.Request("storage.keys", (call, _) => WithStore(call, store =>
        {
            var keys = new JsonArray();
            foreach (var (key, _) in store) keys.Add((JsonNode)JsonValue.Create(key));
            return PluginReply.Of(keys);
        }, write: false));
        router.Request("storage.set", (call, _) => WithStore(call, store =>
        {
            store[call.RequiredString("key")] = call["value"]?.DeepClone();
            return PluginReply.Empty;
        }, write: true));
        router.Request("storage.delete", (call, _) => WithStore(call, store =>
        {
            store.Remove(call.RequiredString("key"));
            return PluginReply.Empty;
        }, write: true));
    }

    public void Reset(PluginHost plugin)
    {
        foreach (var key in _stores.Keys.Where(k => k.StartsWith(plugin.DataDirectory, StringComparison.Ordinal)).ToList())
        {
            _stores.TryRemove(key, out _);
        }
    }

    private async Task<PluginReply> WithStore(PluginCall call, Func<JsonObject, PluginReply> action, bool write)
    {
        var file = FileOf(call);
        var gate = _locks.GetOrAdd(file, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var store = _stores.GetOrAdd(file, Load);
            if (!write) return action(store);
            var backup = (JsonObject)store.DeepClone();
            var reply = action(store);
            var text = store.ToJsonString();
            if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxStoreBytes)
            {
                _stores[file] = backup;
                throw new PluginApiException("TooLarge", $"A plugin store is limited to {MaxStoreBytes / 1024} KB; keep large data in the data directory instead.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = $"{file}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temp, text).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);
            return reply;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string FileOf(PluginCall call)
    {
        var scope = call.String("scope") ?? "global";
        if (scope == "global") return Path.Combine(call.Plugin.DataDirectory, "state.json");
        var project = call.RequiredString("projectId");
        if (project.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || project.Contains("..") || project.Length > 128)
        {
            throw PluginApiException.Invalid("Not a project id.");
        }
        return Path.Combine(call.Plugin.DataDirectory, "projects", $"{project}.json");
    }

    private static JsonObject Load(string file)
    {
        try
        {
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject stored) return stored;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new JsonObject();
    }
}
