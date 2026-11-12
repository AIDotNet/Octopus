namespace Octopus.Core.Plugins;

/// <summary>
/// The content of plugins.json: which plugins are installed and enabled, and what each was granted. Kept apart from
/// settings.json because this is security state, not preference (design/plugins/03-清单与生命周期.md §6).
/// A damaged file is replaced by an empty document: no plugin is enabled and nothing is granted.
/// </summary>
public sealed class PluginsDocument
{
    public int Version { get; set; } = 1;
    /// <summary>Plugin id → record.</summary>
    public Dictionary<string, PluginRecord> Plugins { get; set; } = [];

    /// <summary>The plugin color themes chosen for each appearance (design/plugins/07-主题.md §5).</summary>
    public PluginThemeChoice Themes { get; set; } = new();

    public PluginRecord? Find(string id) => Plugins.GetValueOrDefault(id);
}

/// <summary><c>&lt;plugin id&gt;/&lt;theme id&gt;</c> per appearance; null is Octopus's own theme.</summary>
public sealed class PluginThemeChoice
{
    public string? Light { get; set; }
    public string? Dark { get; set; }
}

public sealed class PluginRecord
{
    public bool Enabled { get; set; }
    /// <summary>The version the grants below were given for; a newer version asks again for what it adds.</summary>
    public string? Version { get; set; }
    public PluginSource Source { get; set; }
    public DateTimeOffset? InstalledAt { get; set; }
    /// <summary>Permission key (see <see cref="PermissionRequest.Key"/>) → granted access, e.g. <c>"read"</c> or <c>"granted"</c>.</summary>
    public Dictionary<string, string> Grants { get; set; } = [];
    /// <summary>Granted locations: external paths, agent data directories, paths picked in a file dialog.</summary>
    public List<PluginPathGrant> PathGrants { get; set; } = [];
    /// <summary>Permission keys the user turned down; not asked again.</summary>
    public List<string> Denied { get; set; } = [];
    /// <summary>Lets the plugin read secret-looking files (.env, keys) inside its granted scopes.</summary>
    public bool IncludeSensitiveFiles { get; set; }
    /// <summary>Recent crash times, for the restart back-off.</summary>
    public List<DateTimeOffset> Crashes { get; set; } = [];
}

/// <param name="Scope"><c>external</c>, <c>agentData</c> or <c>picked</c>.</param>
/// <param name="Agent">For <c>agentData</c>: the agent whose directory this is.</param>
public sealed record PluginPathGrant(string Scope, string Path, FileAccessKind Access, string? Agent = null);
