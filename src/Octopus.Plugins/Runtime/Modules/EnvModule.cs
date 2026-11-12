using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Core.Plugins;
using Octopus.Storage;

namespace Octopus.Plugins.Runtime.Modules;

/// <summary><c>octopus.env</c> and <c>octopus.l10n</c>: facts about the app, and the plugin's own translations.</summary>
public sealed class EnvModule : IPluginApiModule
{
    private readonly PluginHostInfo _host;
    private readonly ISettingsStore _settings;
    private readonly ConcurrentDictionary<PluginHost, byte> _languageListeners = new();
    private readonly ConcurrentDictionary<(string Plugin, string Language), string> _bundles = new();
    private string _language;

    public EnvModule(PluginHostInfo host, ISettingsStore settings)
    {
        _host = host;
        _settings = settings;
        _language = Language;
        settings.Changed += (_, _) =>
        {
            var now = Language;
            if (now == _language) return;
            _language = now;
            _bundles.Clear();
            foreach (var plugin in _languageListeners.Keys) plugin.Emit("env.language", JsonValue.Create(now));
        };
    }

    private string Language => _settings.Current.Appearance.Language is { Length: > 0 } language ? language : "zh-CN";

    public static string Platform =>
        OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux";

    public void Register(PluginApiRouter router)
    {
        router.Sync("env", call => new JsonObject
        {
            ["appVersion"] = _host.AppVersion.ToString(),
            ["apiVersion"] = _host.ApiVersion,
            ["platform"] = Platform,
            ["language"] = Language,
            ["isDev"] = call.Plugin.Package.Source == PluginSource.Dev,
        }.ToJsonString());
        router.Sync("l10n.bundle", call => _bundles.GetOrAdd((call.Plugin.Id, Language), key => LoadBundle(call.Plugin.Package.Directory, key.Language)));
    }

    public void Subscribed(PluginHost plugin, PluginSubscription subscription)
    {
        if (subscription.Event == "env.language") _languageListeners[plugin] = 0;
    }

    public void Reset(PluginHost plugin) => _languageListeners.TryRemove(plugin, out _);

    /// <summary>
    /// <c>l10n/&lt;language&gt;.json</c> of the package: the exact tag, then the language prefix, then <c>en</c>. Read once
    /// per language and kept (package files are local and at most 1 MB).
    /// </summary>
    private static string LoadBundle(string packageDirectory, string language)
    {
        var prefix = language.Split('-', '_')[0];
        foreach (var name in new[] { language, prefix, "en" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.Combine(packageDirectory, "l10n", $"{name}.json");
            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length > PluginApi.MaxManifestBytes) continue;
                var text = File.ReadAllText(file);
                if (JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject bundle)
                {
                    return bundle.ToJsonString();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }
        return "{}";
    }
}
