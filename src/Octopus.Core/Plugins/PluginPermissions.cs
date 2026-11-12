namespace Octopus.Core.Plugins;

/// <summary>How the permission is presented when the user is asked (design/plugins/04-权限与文件系统.md §2).</summary>
public enum PermissionRisk
{
    /// <summary>Listed, checked by default.</summary>
    Low,
    /// <summary>Listed with a one-line explanation, checked by default.</summary>
    Medium,
    /// <summary>Its own group, warning color, not checked by default, consequences spelled out.</summary>
    High,
}

/// <summary>One thing the manifest asks for, flattened for the consent dialog and the grant store.</summary>
/// <param name="Key">Stable key, e.g. <c>git</c>, <c>terminal.write</c>, <c>fs.project</c>, <c>agents.signals.claude.statusline</c>.</param>
/// <param name="Details">What exactly: hosts, programs, paths or access levels.</param>
/// <param name="Reason">The plugin's own explanation, shown as written (only some permissions have one).</param>
public sealed record PermissionRequest(
    string Key,
    PermissionRisk Risk,
    bool Optional,
    IReadOnlyList<string> Details,
    string? Reason = null,
    PluginLocalized? Localized = null);

/// <summary>
/// The <c>permissions</c> block of a manifest. <see cref="Requests"/> lists everything flat; the file scopes are also
/// kept structured because the access policy needs their globs.
/// </summary>
public sealed class PluginPermissions
{
    public static readonly PluginPermissions None = new();

    public IReadOnlyList<PermissionRequest> Requests { get; init; } = [];
    public FsProjectRequest? FsProject { get; init; }
    public IReadOnlyList<FsPathRequest> FsExternal { get; init; } = [];
    public IReadOnlyList<FsAgentDataRequest> FsAgentData { get; init; } = [];

    public bool Any => Requests.Count > 0;
}

/// <summary>The current project's directory, optionally narrowed by globs.</summary>
public sealed record FsProjectRequest(FileAccessKind Access, IReadOnlyList<string> Include, IReadOnlyList<string> Exclude, bool Optional);

/// <summary>A fixed location outside projects. <c>~</c> means the user's home.</summary>
public sealed record FsPathRequest(string Path, FileAccessKind Access, string Reason, bool Optional, PluginLocalized Localized);

/// <summary>Files under an agent's data directory, wherever that directory is on this machine.</summary>
public sealed record FsAgentDataRequest(string Agent, IReadOnlyList<string> Include, FileAccessKind Access, string? Reason, bool Optional, PluginLocalized Localized);
