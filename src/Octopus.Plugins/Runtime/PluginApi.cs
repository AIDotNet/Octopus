using System.Text.Json;
using System.Text.Json.Nodes;

namespace Octopus.Plugins.Runtime;

/// <summary>
/// A failed host call, as the plugin sees it: <c>OctopusError</c> with a stable <see cref="Code"/>
/// (design/plugins/02-运行时.md §3.3). Host exception texts are never passed through.
/// </summary>
public sealed class PluginApiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
    public string? Path { get; init; }
    public string? Permission { get; init; }
    public bool? CanRequest { get; init; }
    public long? RetryAfterMs { get; init; }

    public static PluginApiException Denied(string permission, bool canRequest, string? path = null) =>
        new("PermissionDenied", path is null ? $"The plugin does not have the \"{permission}\" permission." : $"The plugin may not access {path}.")
        { Permission = permission, CanRequest = canRequest, Path = path };

    public static PluginApiException Invalid(string message) => new("InvalidArgument", message);
    public static PluginApiException Unsupported(string what) => new("Unsupported", $"{what} is not available in this version of Octopus.");

    public string ToJson()
    {
        var o = new JsonObject { ["code"] = Code, ["message"] = Message };
        if (Path is not null) o["path"] = Path;
        if (Permission is not null) o["permission"] = Permission;
        if (CanRequest is not null) o["canRequest"] = CanRequest;
        if (RetryAfterMs is not null) o["retryAfterMs"] = RetryAfterMs;
        return o.ToJsonString();
    }
}

/// <summary>The answer to a request: JSON, or a large string in <see cref="Text"/> (then <see cref="Json"/> is empty).</summary>
public readonly record struct PluginReply(string Json, string Text = "")
{
    public static readonly PluginReply Empty = new("");
    public static PluginReply Of(JsonNode? value) => new(value?.ToJsonString() ?? "null");
    public static PluginReply Of(string value) => new(JsonValue.Create(value).ToJsonString());
    public static PluginReply Of(bool value) => new(value ? "true" : "false");
    public static PluginReply OfText(string text) => new("", text);
}

/// <summary>One call from a plugin, with lenient accessors over its JSON arguments.</summary>
public sealed class PluginCall(PluginHost plugin, string method, string json, string text)
{
    private JsonNode? _args;
    private bool _parsed;

    public PluginHost Plugin { get; } = plugin;
    public string Method { get; } = method;
    public string Json { get; } = json;
    /// <summary>A large string argument passed outside the JSON (file contents, request bodies).</summary>
    public string Text { get; } = text;

    public JsonNode? Args
    {
        get
        {
            if (!_parsed)
            {
                _parsed = true;
                try { _args = string.IsNullOrEmpty(Json) ? null : JsonNode.Parse(Json); }
                catch (JsonException) { throw PluginApiException.Invalid("The arguments are not valid JSON."); }
            }
            return _args;
        }
    }

    public JsonNode? this[string name] => Args is JsonObject o ? o[name] : null;

    public string? String(string name) => this[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public string RequiredString(string name) =>
        String(name) is { Length: > 0 } s ? s : throw PluginApiException.Invalid($"\"{name}\" is required.");

    public bool Bool(string name, bool fallback = false) => this[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    public long? Long(string name) => this[name] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? (long)d : null;

    public IReadOnlyList<string> Strings(string name) =>
        this[name] is JsonArray a ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() : [];
}

public delegate Task<PluginReply> PluginRequestHandler(PluginCall call, CancellationToken cancellationToken);

/// <summary>Maps <c>namespace.method</c> names to the host modules that implement them.</summary>
public sealed class PluginApiRouter
{
    private readonly Dictionary<string, PluginRequestHandler> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action<PluginCall>> _notifications = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<PluginCall, string>> _syncs = new(StringComparer.Ordinal);
    private readonly List<IPluginApiModule> _modules = [];

    public PluginApiRouter(IEnumerable<IPluginApiModule> modules)
    {
        foreach (var module in modules)
        {
            _modules.Add(module);
            module.Register(this);
        }
    }

    public IReadOnlyList<IPluginApiModule> Modules => _modules;

    public void Request(string method, PluginRequestHandler handler) => _requests[method] = handler;
    public void Notify(string method, Action<PluginCall> handler) => _notifications[method] = handler;
    /// <summary>A synchronous snapshot read. The handler runs on the plugin's thread: memory only, no IO, no UI thread.</summary>
    public void Sync(string method, Func<PluginCall, string> handler) => _syncs[method] = handler;

    internal PluginRequestHandler? FindRequest(string method) => _requests.GetValueOrDefault(method);
    internal Action<PluginCall>? FindNotify(string method) => _notifications.GetValueOrDefault(method);
    internal Func<PluginCall, string>? FindSync(string method) => _syncs.GetValueOrDefault(method);
}

/// <summary>A host module: registers the methods it implements and drops a plugin's state when the plugin stops.</summary>
public interface IPluginApiModule
{
    void Register(PluginApiRouter router);

    /// <summary>The plugin stopped (deactivated, crashed or disabled): forget everything it registered here.</summary>
    void Reset(PluginHost plugin) { }

    /// <summary>The plugin started listening to <paramref name="subscription"/> (e.g. start a file watcher).</summary>
    void Subscribed(PluginHost plugin, PluginSubscription subscription) { }

    void Unsubscribed(PluginHost plugin, PluginSubscription subscription) { }
}

/// <summary>An event subscription of a plugin; <see cref="Key"/> is what the plugin receives events under.</summary>
public sealed record PluginSubscription(string Event, string Key, JsonNode? Args);
