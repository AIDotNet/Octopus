namespace Octopus.Core.Plugins;

/// <summary>
/// What a plugin adds to Octopus (<c>contributes</c>, design/plugins/03-清单与生命周期.md §3). Declarative contributions
/// work without running any plugin code; the others name a provider that the plugin registers in <c>activate()</c>.
/// Ids are the plugin id or start with it plus a dot.
/// </summary>
public sealed class PluginContributions
{
    public static readonly PluginContributions Empty = new();

    // ---- declarative

    public IReadOnlyList<ThemeContribution> Themes { get; init; } = [];
    public IReadOnlyList<IconThemeContribution> IconThemes { get; init; } = [];
    /// <summary>Agent definition files, same schema as <c>&lt;data&gt;/agents/*.json</c>.</summary>
    public IReadOnlyList<string> Agents { get; init; } = [];
    public IReadOnlyList<AgentFeaturesContribution> AgentFeatures { get; init; } = [];
    public IReadOnlyList<PromptContribution> Prompts { get; init; } = [];
    public IReadOnlyList<AgentPresetContribution> AgentPresets { get; init; } = [];
    public IReadOnlyList<TerminalProfileContribution> TerminalProfiles { get; init; } = [];
    public IReadOnlyList<TerminalLinkContribution> TerminalLinks { get; init; } = [];
    public IReadOnlyList<KeybindingContribution> Keybindings { get; init; } = [];
    /// <summary>Menu location (<c>launcher</c>, <c>explorer/context</c>…) → entries.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<MenuEntryContribution>> Menus { get; init; } = new Dictionary<string, IReadOnlyList<MenuEntryContribution>>();
    public IReadOnlyList<ConfigurationProperty> Configuration { get; init; } = [];
    public IReadOnlyList<LanguageContribution> Languages { get; init; } = [];

    // ---- need code

    public IReadOnlyList<CommandContribution> Commands { get; init; } = [];
    public IReadOnlyList<ViewContribution> Views { get; init; } = [];
    public IReadOnlyList<EditorContribution> Editors { get; init; } = [];
    public IReadOnlyList<NamedContribution> Panels { get; init; } = [];
    public IReadOnlyList<NamedContribution> SidebarSections { get; init; } = [];
    public IReadOnlyList<NamedContribution> Indicators { get; init; } = [];
    public IReadOnlyList<UsageProviderContribution> UsageProviders { get; init; } = [];
    public IReadOnlyList<NamedContribution> TaskProviders { get; init; } = [];
    public IReadOnlyList<NamedContribution> StatusProviders { get; init; } = [];

    /// <summary>True when the plugin has nothing that needs its code to run.</summary>
    public bool IsDeclarativeOnly =>
        Commands.Count == 0 && Views.Count == 0 && Editors.Count == 0 && Panels.Count == 0 && SidebarSections.Count == 0
        && Indicators.Count == 0 && UsageProviders.Count == 0 && TaskProviders.Count == 0 && StatusProviders.Count == 0;
}

public enum ThemeAppearance
{
    Light,
    Dark,
}

/// <summary>A color theme file (design/plugins/07-主题.md).</summary>
public sealed record ThemeContribution(string Id, string Label, ThemeAppearance Appearance, string Path, PluginLocalized Localized);

public sealed record IconThemeContribution(string Id, string Label, string Path, PluginLocalized Localized);

/// <summary>Extra <c>features[]</c> or <c>env.known[]</c> entries for an existing agent, from a file.</summary>
public sealed record AgentFeaturesContribution(string Agent, string Path);

/// <summary>A prompt template offered next to the prompt box. Variables: <c>{file}</c>, <c>{selection}</c>, <c>{project}</c>…</summary>
public sealed record PromptContribution(string Id, string Title, string? Description, string Text, IReadOnlyList<string> Agents, PluginLocalized Localized);

/// <summary>A named launch: agent with model, effort and an optional prompt template.</summary>
public sealed record AgentPresetContribution(string Id, string Title, string Agent, string? Model, string? Effort, string? Prompt, PluginLocalized Localized);

/// <summary>A named terminal start: command, arguments, environment, directory relative to the project.</summary>
public sealed record TerminalProfileContribution(
    string Id,
    string Title,
    string? Icon,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env,
    string? Cwd,
    PluginLocalized Localized);

public enum TerminalLinkKind
{
    File,
    Url,
}

/// <summary>A regular expression over terminal lines that becomes a link (run by the host, never by plugin code).</summary>
public sealed record TerminalLinkContribution(string Pattern, TerminalLinkKind Kind, string Template);

/// <summary>A default key for a command. <c>Mac</c> and <c>Other</c> use the <c>cmd+shift+e</c> notation.</summary>
public sealed record KeybindingContribution(string Command, string? Mac, string? Other, string? When);

public sealed record MenuEntryContribution(string Command, string? When, string? Group, int Order);

/// <summary>A setting of the plugin, rendered by the same field view models as the agent settings.</summary>
/// <param name="DefaultJson">The default value as JSON text; null when there is none.</param>
public sealed record ConfigurationProperty(
    string Key,
    ConfigurationType Type,
    string Label,
    string? Description,
    string? DefaultJson,
    IReadOnlyList<string> Options,
    PluginLocalized Localized);

public enum ConfigurationType
{
    String,
    Text,
    Bool,
    Number,
    Enum,
    File,
    List,
    Secret,
    /// <summary>
    /// A command line only the user enters (in Settings → Plugins). The plugin can neither read nor change it; it can
    /// only ask the host to run it and gets the output (<c>config.runCommand</c>). Used for user-defined data sources.
    /// </summary>
    Command,
}

/// <summary>An interface translation of Octopus itself (an XLIFF file).</summary>
public sealed record LanguageContribution(string Language, string Path);

public sealed record CommandContribution(string Id, string Title, string? Icon, string? Category, PluginLocalized Localized);

public enum ViewSingleton
{
    None,
    Project,
    App,
}

/// <summary>A plugin tab type (design/plugins/06-标签与渲染.md §3).</summary>
public sealed record ViewContribution(string Id, string Title, string? Icon, bool Launcher, ViewSingleton Singleton, bool Restore, PluginLocalized Localized);

public enum EditorPriority
{
    /// <summary>Becomes the default way to open matching files.</summary>
    Default,
    /// <summary>Only listed under "Open with".</summary>
    Option,
}

public enum FileAccessKind
{
    Read,
    ReadWrite,
}

/// <summary>A way to open files (design/plugins/06-标签与渲染.md §2).</summary>
/// <param name="Selector">File name or project-relative globs such as <c>*.csv</c> or <c>**/data/*.txt</c>.</param>
public sealed record EditorContribution(
    string Id,
    string Title,
    string? Icon,
    IReadOnlyList<string> Selector,
    EditorPriority Priority,
    FileAccessKind Access,
    long? MaxSize,
    bool Binary,
    PluginLocalized Localized);

/// <summary>A contribution that only needs an id, a title and an icon in the manifest (panels, indicators, providers…).</summary>
public sealed record NamedContribution(string Id, string? Title, string? Icon, PluginLocalized Localized);

public sealed record UsageProviderContribution(string Id, string Agent);
