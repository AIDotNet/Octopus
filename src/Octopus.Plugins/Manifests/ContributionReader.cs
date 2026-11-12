using System.Text.Json;
using System.Text.RegularExpressions;
using Octopus.Core.Plugins;

namespace Octopus.Plugins.Manifests;

/// <summary>
/// Reads <c>contributes</c> (design/plugins/03-清单与生命周期.md §3). An invalid contribution is reported as an error and
/// left out; unknown contribution kinds are warnings, so a manifest written for a newer Octopus still loads.
/// </summary>
internal sealed class ContributionReader(string pluginId, string? packageDirectory)
{
    private static readonly string[] KnownKinds =
    [
        "themes", "iconThemes", "agents", "agentFeatures", "prompts", "agentPresets", "terminalProfiles", "terminalLinks",
        "keybindings", "menus", "configuration", "languages",
        "commands", "views", "editors", "panels", "sidebarSections", "indicators", "usageProviders", "taskProviders", "statusProviders",
    ];

    /// <summary>Menu locations (design/plugins/08-扩展点总览.md §2).</summary>
    private static readonly string[] MenuLocations =
    [
        "launcher", "toolbar", "explorer/context", "explorer/title", "search/context", "git/change/context", "git/title",
        "sidebar/item/context", "sidebar/project/context", "editor/title", "terminal/context", "emptyProject", "tab/context",
    ];

    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    /// <summary>Commands a keybinding or menu of this plugin may name besides Octopus's own (<c>octopus.*</c>).</summary>
    private readonly HashSet<string> _commands = new(StringComparer.Ordinal);

    public PluginContributions Read(ManifestObject? block)
    {
        if (block is not { } c)
        {
            return PluginContributions.Empty;
        }
        c.WarnUnknown(KnownKinds);
        var commands = Each(c, "commands", item => new CommandContribution(
            item.String("id", true)!, item.String("title", true)!, item.String("icon"), item.String("category"), item.Localized()),
            "id", "title", "icon", "category", "localized");
        var views = Each(c, "views", ReadView, "id", "title", "icon", "launcher", "singleton", "restore", "localized");
        _commands.UnionWith(commands.Select(command => command.Id));
        _commands.UnionWith(views.Select(view => $"{view.Id}.open"));
        var prompts = Each(c, "prompts", item => new PromptContribution(
            item.String("id", true)!, item.String("title", true)!, item.String("description"), item.String("text", true)!,
            item.Strings("agents"), item.Localized()),
            "id", "title", "description", "text", "agents", "localized");

        var result = new PluginContributions
        {
            Themes = Each(c, "themes", ReadTheme, "id", "label", "appearance", "path", "localized"),
            IconThemes = Each(c, "iconThemes", item => new IconThemeContribution(
                item.String("id", true)!, item.String("label", true)!, File(item, "path")!, item.Localized()),
                "id", "label", "path", "localized"),
            Agents = ReadAgentFiles(c),
            AgentFeatures = Each(c, "agentFeatures", item => new AgentFeaturesContribution(item.String("agent", true)!, File(item, "path")!),
                "agent", "path"),
            Prompts = prompts,
            AgentPresets = Each(c, "agentPresets", item => ReadPreset(item, prompts), "id", "title", "agent", "model", "effort", "prompt", "localized"),
            TerminalProfiles = Each(c, "terminalProfiles", ReadTerminalProfile, "id", "title", "icon", "command", "args", "env", "cwd", "localized"),
            TerminalLinks = Each(c, "terminalLinks", ReadTerminalLink, "pattern", "kind", "template"),
            Keybindings = Each(c, "keybindings", ReadKeybinding, "command", "mac", "other", "when"),
            Menus = ReadMenus(c),
            Configuration = ReadConfiguration(c),
            Languages = Each(c, "languages", item => new LanguageContribution(item.String("language", true)!, File(item, "path")!), "language", "path"),
            Commands = commands,
            Views = views,
            Editors = Each(c, "editors", ReadEditor, "id", "title", "icon", "selector", "priority", "access", "maxSize", "binary", "localized"),
            Panels = Named(c, "panels"),
            SidebarSections = Named(c, "sidebarSections"),
            Indicators = Named(c, "indicators"),
            UsageProviders = Each(c, "usageProviders", item => new UsageProviderContribution(item.String("id", true)!, item.String("agent", true)!), "id", "agent"),
            TaskProviders = Named(c, "taskProviders"),
            StatusProviders = Named(c, "statusProviders"),
        };
        return result;
    }

    /// <summary>Warns when a keybinding or menu entry names a command this plugin does not declare (it may belong to another plugin).</summary>
    private string? Command(ManifestObject item)
    {
        var command = item.String("command", required: true);
        if (command is not null && !_commands.Contains(command) && !command.StartsWith("octopus.", StringComparison.Ordinal))
        {
            item.Log.Warn(item.At("command"), $"\"{command}\" is not declared in this plugin's commands");
        }
        return command;
    }

    // ---------------------------------------------------------------- generic

    /// <summary>
    /// Reads every object of an array contribution. An item with any error is left out; an id is checked for the
    /// plugin prefix and for duplicates.
    /// </summary>
    private IReadOnlyList<T> Each<T>(ManifestObject c, string kind, Func<ManifestObject, T> read, params string[] known)
    {
        var list = new List<T>();
        foreach (var item in c.Objects(kind))
        {
            item.WarnUnknown(known);
            var before = ErrorCount(item);
            if (known.Contains("id") && item.Raw("id") is { ValueKind: JsonValueKind.String } id && !CheckId(item, id.GetString()!))
            {
                continue;
            }
            // Missing required fields come back as null and are logged as errors: such items are left out.
            var value = read(item);
            if (ErrorCount(item) == before)
            {
                list.Add(value);
            }
        }
        return list;
    }

    private static int ErrorCount(ManifestObject item) => item.Log.Issues.Count(i => i.Severity == IssueSeverity.Error);

    private IReadOnlyList<NamedContribution> Named(ManifestObject c, string kind) =>
        Each(c, kind, item => new NamedContribution(item.String("id", true)!, item.String("title"), item.String("icon"), item.Localized()),
            "id", "title", "icon", "localized");

    private bool CheckId(ManifestObject item, string id)
    {
        if (id != pluginId && !id.StartsWith(pluginId + ".", StringComparison.Ordinal))
        {
            item.Log.Error(item.At("id"), $"\"{id}\" must be \"{pluginId}\" or start with \"{pluginId}.\"");
            return false;
        }
        if (!_ids.Add(id))
        {
            item.Log.Error(item.At("id"), $"\"{id}\" is used by another contribution of this plugin");
            return false;
        }
        return true;
    }

    /// <summary>A file of the package; checked to exist inside it when the package directory is known.</summary>
    private string? File(ManifestObject item, string name)
    {
        var path = item.String(name, required: true);
        if (path is null || packageDirectory is null)
        {
            return path;
        }
        if (!PackagePath.TryResolve(packageDirectory, path, out _, out var error))
        {
            item.Log.Error(item.At(name), $"\"{path}\" {error}");
            return null;
        }
        return path;
    }

    private static T Choice<T>(ManifestObject item, string name, T fallback, params (string Text, T Value)[] choices)
    {
        var text = item.String(name);
        if (text is null)
        {
            return fallback;
        }
        foreach (var (choiceText, value) in choices)
        {
            if (choiceText == text) return value;
        }
        item.Log.Error(item.At(name), $"\"{text}\" must be one of {string.Join(", ", choices.Select(c => $"\"{c.Text}\""))}");
        return fallback;
    }

    // ---------------------------------------------------------------- kinds

    private ThemeContribution ReadTheme(ManifestObject item)
    {
        var appearance = item.String("appearance", required: true);
        ThemeAppearance? parsed = appearance switch
        {
            "light" => ThemeAppearance.Light,
            "dark" => ThemeAppearance.Dark,
            _ => null,
        };
        if (appearance is not null && parsed is null)
        {
            item.Log.Error(item.At("appearance"), "must be \"light\" or \"dark\": a theme file is designed for one of them");
        }
        return new ThemeContribution(item.String("id", true)!, item.String("label", true)!, parsed ?? ThemeAppearance.Dark, File(item, "path")!, item.Localized());
    }

    private IReadOnlyList<string> ReadAgentFiles(ManifestObject c)
    {
        var paths = new List<string>();
        if (c.Raw("agents") is not { } value)
        {
            return paths;
        }
        if (value.ValueKind != JsonValueKind.Array)
        {
            c.Log.Error(c.At("agents"), "must be an array of agent definition files");
            return paths;
        }
        var i = 0;
        foreach (var entry in value.EnumerateArray())
        {
            var at = $"{c.At("agents")}[{i++}]";
            var path = entry.ValueKind switch
            {
                JsonValueKind.String => entry.GetString(),
                JsonValueKind.Object when entry.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String => p.GetString(),
                _ => null,
            };
            if (path is null)
            {
                c.Log.Error(at, "must be a file path or {\"path\": \"…\"}");
                continue;
            }
            if (packageDirectory is not null && !PackagePath.TryResolve(packageDirectory, path, out _, out var error))
            {
                c.Log.Error(at, $"\"{path}\" {error}");
                continue;
            }
            paths.Add(path);
        }
        return paths;
    }

    private static AgentPresetContribution ReadPreset(ManifestObject item, IReadOnlyList<PromptContribution> prompts)
    {
        var prompt = item.String("prompt");
        if (prompt is not null && prompts.All(p => p.Id != prompt))
        {
            item.Log.Warn(item.At("prompt"), $"\"{prompt}\" is not one of this plugin's prompts");
        }
        return new AgentPresetContribution(item.String("id", true)!, item.String("title", true)!, item.String("agent", true)!,
            item.String("model"), item.String("effort"), prompt, item.Localized());
    }

    private static TerminalProfileContribution ReadTerminalProfile(ManifestObject item)
    {
        var cwd = item.String("cwd");
        if (cwd is not null && (Path.IsPathRooted(cwd) || cwd.StartsWith('~')))
        {
            item.Log.Error(item.At("cwd"), "must be relative to the project directory");
        }
        return new TerminalProfileContribution(item.String("id", true)!, item.String("title", true)!, item.String("icon"),
            item.String("command", true)!, item.Strings("args"), item.StringMap("env"), cwd, item.Localized());
    }

    private static TerminalLinkContribution ReadTerminalLink(ManifestObject item)
    {
        var pattern = item.String("pattern", required: true);
        if (pattern is not null)
        {
            try
            {
                _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException e)
            {
                item.Log.Error(item.At("pattern"), $"is not a valid regular expression: {e.Message}");
            }
        }
        var kind = Choice(item, "kind", TerminalLinkKind.Url, ("file", TerminalLinkKind.File), ("url", TerminalLinkKind.Url));
        return new TerminalLinkContribution(pattern!, kind, item.String("template", true)!);
    }

    private KeybindingContribution ReadKeybinding(ManifestObject item)
    {
        var mac = item.String("mac");
        var other = item.String("other");
        if (mac is null && other is null)
        {
            item.Log.Error(item.Path, "needs \"mac\", \"other\" or both");
        }
        return new KeybindingContribution(Command(item)!, mac, other, item.String("when"));
    }

    private static ViewContribution ReadView(ManifestObject item) => new(
        item.String("id", true)!, item.String("title", true)!, item.String("icon"), item.Bool("launcher"),
        Choice(item, "singleton", ViewSingleton.None, ("none", ViewSingleton.None), ("project", ViewSingleton.Project), ("app", ViewSingleton.App)),
        item.Bool("restore", fallback: true), item.Localized());

    private static EditorContribution ReadEditor(ManifestObject item)
    {
        var selector = item.Strings("selector").Where(s => s.Trim().Length > 0).ToList();
        if (selector.Count == 0)
        {
            item.Log.Error(item.At("selector"), "must list at least one file pattern, e.g. \"*.csv\"");
        }
        return new EditorContribution(
            item.String("id", true)!, item.String("title", true)!, item.String("icon"), selector,
            Choice(item, "priority", EditorPriority.Default, ("default", EditorPriority.Default), ("option", EditorPriority.Option)),
            Choice(item, "access", FileAccessKind.Read, ("read", FileAccessKind.Read), ("readwrite", FileAccessKind.ReadWrite)),
            item.Integer("maxSize", min: 1), item.Bool("binary"), item.Localized());
    }

    private IReadOnlyDictionary<string, IReadOnlyList<MenuEntryContribution>> ReadMenus(ManifestObject c)
    {
        var menus = new Dictionary<string, IReadOnlyList<MenuEntryContribution>>(StringComparer.Ordinal);
        if (c.Object("menus") is not { } block)
        {
            return menus;
        }
        foreach (var (location, _) in block.Properties())
        {
            if (!MenuLocations.Contains(location, StringComparer.Ordinal))
            {
                block.Log.Warn(block.At(location), "is not a menu location known to this version of Octopus; ignored");
                continue;
            }
            var entries = Each(block, location, item => new MenuEntryContribution(
                Command(item)!, item.String("when"), item.String("group"), (int)(item.Integer("order") ?? 0)),
                "command", "when", "group", "order");
            if (entries.Count > 0) menus[location] = entries;
        }
        return menus;
    }

    private IReadOnlyList<ConfigurationProperty> ReadConfiguration(ManifestObject c)
    {
        var list = new List<ConfigurationProperty>();
        if (c.Object("configuration") is not { } configuration)
        {
            return list;
        }
        configuration.WarnUnknown("properties");
        if (configuration.Object("properties") is not { } properties)
        {
            return list;
        }
        foreach (var (key, _) in properties.Properties())
        {
            if (properties.Object(key) is not { } item) continue;
            item.WarnUnknown("type", "label", "description", "default", "options", "localized");
            if (!key.StartsWith(pluginId + ".", StringComparison.Ordinal))
            {
                item.Log.Error(item.Path, $"setting keys must start with \"{pluginId}.\"");
                continue;
            }
            var before = ErrorCount(item);
            var type = Choice(item, "type", ConfigurationType.String,
                ("string", ConfigurationType.String), ("text", ConfigurationType.Text), ("bool", ConfigurationType.Bool),
                ("number", ConfigurationType.Number), ("enum", ConfigurationType.Enum), ("file", ConfigurationType.File),
                ("list", ConfigurationType.List), ("secret", ConfigurationType.Secret), ("command", ConfigurationType.Command));
            if (!item.Has("type")) item.Log.Error(item.At("type"), "is required");
            var options = item.Strings("options");
            if (type == ConfigurationType.Enum && options.Count == 0) item.Log.Error(item.At("options"), "an enum needs its options");
            var @default = item.Raw("default");
            if (@default is { } value && !DefaultMatches(type, value, options))
            {
                item.Log.Error(item.At("default"), $"does not fit the type \"{type.ToString().ToLowerInvariant()}\"");
            }
            if (type == ConfigurationType.Secret && @default is not null)
            {
                item.Log.Error(item.At("default"), "a secret cannot have a default value");
            }
            // A default command would be one the plugin chose and the host ran: only the user writes commands.
            if (type == ConfigurationType.Command && @default is not null)
            {
                item.Log.Error(item.At("default"), "a command cannot have a default value");
            }
            var label = item.String("label", required: true);
            if (ErrorCount(item) == before)
            {
                list.Add(new ConfigurationProperty(key, type, label!, item.String("description"), @default?.GetRawText(), options, item.Localized()));
            }
        }
        return list;
    }

    private static bool DefaultMatches(ConfigurationType type, JsonElement value, IReadOnlyList<string> options) => type switch
    {
        ConfigurationType.Bool => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        ConfigurationType.Number => value.ValueKind == JsonValueKind.Number,
        ConfigurationType.List => value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String),
        ConfigurationType.Enum => value.ValueKind == JsonValueKind.String && options.Contains(value.GetString()!),
        _ => value.ValueKind == JsonValueKind.String,
    };
}
