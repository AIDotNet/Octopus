using Octopus.Core.Plugins;

namespace Octopus.Plugins.Security;

/// <summary>The kinds of file scope a plugin can hold (design/plugins/04 §3.1, 11 §4).</summary>
public enum FileScopeKind
{
    /// <summary>The plugin's own package directory. Always held, always read-only.</summary>
    Plugin,
    /// <summary>The plugin's private data directory (<c>plugin-data/&lt;id&gt;/files/</c>). Always held.</summary>
    Data,
    /// <summary>The plugin's temporary directory. Always held.</summary>
    Tmp,
    /// <summary>The one file the person opened with the plugin's editor, while its tab exists.</summary>
    Document,
    /// <summary>A file or folder the person chose in a dialog the plugin asked for.</summary>
    Picked,
    /// <summary>A project directory; declared in the manifest, granted by the person.</summary>
    Project,
    /// <summary>Files below an agent's data directory, wherever that is on this machine.</summary>
    AgentData,
    /// <summary>A fixed location outside projects, declared and granted path by path.</summary>
    External,
}

/// <summary>
/// One location a plugin may reach: everything at or below <see cref="Root"/>, narrowed by the globs. The host builds
/// these from the manifest, <c>plugins.json</c> and what is open; the access policy only reads them.
/// </summary>
/// <param name="Root">An absolute path: a directory, or a single file (<see cref="FileScopeKind.Document"/>, a picked file).</param>
public sealed record FileScope(FileScopeKind Kind, string Root, FileAccessKind Access)
{
    /// <summary>Globs relative to <see cref="Root"/> (<see cref="FileSystem.PathGlob"/> syntax); empty means everything.</summary>
    public IReadOnlyList<string> Include { get; init; } = [];

    /// <summary>Globs relative to <see cref="Root"/>; a path is out when it or one of its parent directories matches.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>For <see cref="FileScopeKind.AgentData"/>: the agent whose directory this is.</summary>
    public string? Agent { get; init; }

    /// <summary>For <see cref="FileScopeKind.AgentData"/>: which of the agent's accounts, when it has several.</summary>
    public string? Account { get; init; }

    /// <summary>Writes are possible at all: never in the plugin's own package, otherwise as granted.</summary>
    public bool AllowsWrite => Kind != FileScopeKind.Plugin && Access == FileAccessKind.ReadWrite;

    /// <summary>
    /// A scope from a manifest path that may end in a glob, such as <c>~/Documents/Notes/**</c>: the part before the
    /// first segment with a wildcard becomes <see cref="Root"/>, the rest the one <see cref="Include"/> glob. <c>~</c>
    /// is <paramref name="homeDirectory"/>; environment variables and relative paths are not supported
    /// (design/plugins/04 §3.2).
    /// </summary>
    /// <exception cref="ArgumentException">The pattern has no fixed absolute directory to start from.</exception>
    public static FileScope FromPattern(FileScopeKind kind, string pattern, FileAccessKind access, string homeDirectory)
    {
        var expanded = pattern == "~" ? homeDirectory
            : pattern.StartsWith("~/", StringComparison.Ordinal) || pattern.StartsWith(@"~\", StringComparison.Ordinal)
                ? Path.Combine(homeDirectory, pattern[2..])
                : pattern;
        if (!Path.IsPathFullyQualified(expanded)) throw new ArgumentException($"'{pattern}' is not an absolute path.", nameof(pattern));
        var wildcard = expanded.AsSpan().IndexOfAny('*', '?');
        if (wildcard < 0) return new FileScope(kind, expanded, access);
        var separator = expanded.AsSpan(0, wildcard).LastIndexOfAny('/', '\\');
        if (separator < 0) throw new ArgumentException($"'{pattern}' has no directory before its wildcard.", nameof(pattern));
        var root = expanded[..separator];
        if (root.Length == 0 || root.EndsWith(':')) root = expanded[..(separator + 1)]; // "/" or "C:\"
        return new FileScope(kind, root, access) { Include = [expanded[(separator + 1)..].Replace('\\', '/')] };
    }
}

/// <summary>
/// The user's home directory as far as plugin file scopes are concerned (<c>~</c> in manifests, agents' default data
/// directories). Replaceable so tests never reach the real one.
/// </summary>
public sealed record UserHome(string Path)
{
    public static UserHome Current { get; } = new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}
