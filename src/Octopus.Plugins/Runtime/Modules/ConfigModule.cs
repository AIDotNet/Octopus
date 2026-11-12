using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Core.Plugins;

namespace Octopus.Plugins.Runtime.Modules;

/// <summary>
/// Values of the settings plugins declare in <c>contributes.configuration</c>, kept per plugin in
/// <c>plugin-data/&lt;id&gt;/settings.json</c>. Shared by the plugin API and the plugin settings page.
/// </summary>
public sealed class PluginConfigStore(PluginPaths paths)
{
    private readonly ConcurrentDictionary<string, JsonObject> _values = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>(plugin id, setting key) after any change.</summary>
    public event EventHandler<(string Plugin, string Key)>? Changed;

    /// <summary>The value, or the manifest's default, or null.</summary>
    public JsonNode? Get(PluginManifest manifest, string key)
    {
        var values = _values.GetOrAdd(manifest.Id, Load);
        lock (_gate)
        {
            if (values[key] is { } value) return value.DeepClone();
        }
        var declared = manifest.Contributes.Configuration.FirstOrDefault(p => p.Key == key);
        return declared?.DefaultJson is { } json ? JsonNode.Parse(json) : null;
    }

    /// <summary>The value the user stored, without the manifest's default; null when there is none.</summary>
    public JsonNode? GetUserValue(PluginManifest manifest, string key)
    {
        var values = _values.GetOrAdd(manifest.Id, Load);
        lock (_gate)
        {
            return values[key]?.DeepClone();
        }
    }

    /// <summary>
    /// Sets a declared setting; null removes the user's value so the default applies again. <paramref name="byUser"/>
    /// is true only for the settings page: a <c>command</c> setting is the user's to write, never the plugin's.
    /// </summary>
    public void Set(PluginManifest manifest, string key, JsonNode? value, bool byUser = false)
    {
        var declared = manifest.Contributes.Configuration.FirstOrDefault(p => p.Key == key)
            ?? throw PluginApiException.Invalid($"\"{key}\" is not a setting declared by {manifest.Id}.");
        if (declared.Type == ConfigurationType.Secret)
        {
            throw PluginApiException.Invalid("Secrets are stored with octopus.secrets, not in settings.");
        }
        if (declared.Type == ConfigurationType.Command && !byUser)
        {
            throw PluginApiException.Denied($"config:{key}", canRequest: false);
        }
        if (value is not null && !Fits(declared, value))
        {
            throw PluginApiException.Invalid($"The value does not fit the {declared.Type.ToString().ToLowerInvariant()} setting \"{key}\".");
        }
        var values = _values.GetOrAdd(manifest.Id, Load);
        string text;
        lock (_gate)
        {
            if (value is null) values.Remove(key);
            else values[key] = value.DeepClone();
            text = values.ToJsonString();
        }
        var file = FileOf(manifest.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = $"{file}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, file, overwrite: true);
        Changed?.Invoke(this, (manifest.Id, key));
    }

    private static bool Fits(ConfigurationProperty property, JsonNode value) => property.Type switch
    {
        ConfigurationType.Bool => value is JsonValue v && v.TryGetValue<bool>(out _),
        ConfigurationType.Number => value is JsonValue v && v.TryGetValue<double>(out _),
        ConfigurationType.List => value is JsonArray a && a.All(i => i is JsonValue s && s.TryGetValue<string>(out _)),
        ConfigurationType.Enum => value is JsonValue v && v.TryGetValue<string>(out var s) && property.Options.Contains(s),
        _ => value is JsonValue v && v.TryGetValue<string>(out _),
    };

    private string FileOf(string id) => Path.Combine(paths.DataDirectoryOf(id), "settings.json");

    private JsonObject Load(string id)
    {
        try
        {
            var file = FileOf(id);
            if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject stored) return stored;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new JsonObject();
    }
}

/// <summary>
/// <c>octopus.config</c>: the plugin reads and writes its own declared settings, and runs the commands the user put
/// into its <c>command</c> settings (without seeing or changing them).
/// </summary>
public sealed class ConfigModule : IPluginApiModule
{
    /// <summary>A plugin may run one user command at most this often (per setting).</summary>
    public static readonly TimeSpan MinCommandInterval = TimeSpan.FromSeconds(10);

    private readonly PluginConfigStore _store;
    private readonly UserCommandRunner? _commands;
    private readonly ConcurrentDictionary<PluginHost, byte> _listeners = new();
    private readonly ConcurrentDictionary<(string Plugin, string Key), DateTimeOffset> _lastRun = new();

    public ConfigModule(PluginConfigStore store, UserCommandRunner? commands = null)
    {
        _store = store;
        _commands = commands;
        store.Changed += (_, change) =>
        {
            foreach (var plugin in _listeners.Keys.Where(p => p.Id == change.Plugin)) plugin.Emit("config.changed", JsonValue.Create(change.Key));
        };
    }

    public void Register(PluginApiRouter router)
    {
        router.Sync("config.get", call =>
        {
            var key = call.RequiredString("key");
            // A command may hold a token (curl -H …): the plugin learns only whether one is set.
            if (Declared(call.Plugin, key)?.Type == ConfigurationType.Command)
            {
                return _store.GetUserValue(call.Plugin.Manifest, key) is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? "true" : "false";
            }
            return _store.Get(call.Plugin.Manifest, key)?.ToJsonString() ?? "";
        });
        router.Request("config.runCommand", RunCommandAsync);
        router.Request("config.set", (call, _) =>
        {
            _store.Set(call.Plugin.Manifest, call.RequiredString("key"), call["value"]?.DeepClone());
            return Task.FromResult(PluginReply.Empty);
        });
    }

    private static ConfigurationProperty? Declared(PluginHost plugin, string key) =>
        plugin.Manifest.Contributes.Configuration.FirstOrDefault(p => p.Key == key);

    private async Task<PluginReply> RunCommandAsync(PluginCall call, CancellationToken cancellationToken)
    {
        var key = call.RequiredString("key");
        if (Declared(call.Plugin, key)?.Type != ConfigurationType.Command)
        {
            throw PluginApiException.Invalid($"\"{key}\" is not a command setting of {call.Plugin.Id}.");
        }
        if (_commands is null) throw PluginApiException.Unsupported("Running user commands");
        // Only what the user typed runs: never a manifest default, never something the plugin wrote.
        if (_store.GetUserValue(call.Plugin.Manifest, key) is not JsonValue v || !v.TryGetValue<string>(out var command) || command.Trim().Length == 0)
        {
            throw new PluginApiException("NotFound", $"No command is set for \"{key}\".");
        }
        var now = DateTimeOffset.UtcNow;
        var last = _lastRun.GetValueOrDefault((call.Plugin.Id, key));
        if (now - last < MinCommandInterval)
        {
            throw new PluginApiException("Busy", "The command ran moments ago.") { RetryAfterMs = (long)(last + MinCommandInterval - now).TotalMilliseconds };
        }
        _lastRun[(call.Plugin.Id, key)] = now;
        var result = await _commands.RunAsync(command, cancellationToken).ConfigureAwait(false);
        return PluginReply.Of(new JsonObject
        {
            ["exitCode"] = result.ExitCode,
            ["stdout"] = result.StandardOutput,
            ["stderr"] = result.StandardError,
            ["timedOut"] = result.TimedOut,
            ["elapsedMs"] = result.ElapsedMilliseconds,
        });
    }

    public void Subscribed(PluginHost plugin, PluginSubscription subscription)
    {
        if (subscription.Event == "config.changed") _listeners[plugin] = 0;
    }

    public void Reset(PluginHost plugin) => _listeners.TryRemove(plugin, out _);
}
