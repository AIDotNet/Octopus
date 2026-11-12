using Octopus.Core.Plugins;
using Octopus.FileSystem;

namespace Octopus.Plugins.Security;

/// <summary>
/// The file scopes one plugin holds right now, plus its "include sensitive files" switch. Immutable: when grants,
/// open documents or picked paths change, the host builds a new set. Scope roots are resolved (links followed) and
/// globs compiled on first use and then cached.
/// </summary>
public sealed class PluginGrantSet
{
    private readonly CompiledScope[]?[] _compiled = new CompiledScope[]?[2];

    public PluginGrantSet(IEnumerable<FileScope> scopes, bool includeSensitiveFiles = false)
    {
        Scopes = [.. scopes];
        IncludeSensitiveFiles = includeSensitiveFiles;
    }

    public IReadOnlyList<FileScope> Scopes { get; }

    /// <summary>The person allowed secret-looking files (<see cref="SensitiveFiles.NamePatterns"/>) in the granted scopes. Never credentials.</summary>
    public bool IncludeSensitiveFiles { get; }

    /// <summary>
    /// The scopes every plugin holds (its package read-only, <c>plugin-data/&lt;id&gt;/files/</c> and its temporary
    /// directory read-write; design/plugins/03 §5) plus <paramref name="granted"/>. The data scope is the
    /// <c>files</c> folder, not <c>plugin-data/&lt;id&gt;</c> itself, so the plugin cannot reach its state files or its
    /// own audit log.
    /// </summary>
    public static PluginGrantSet ForPlugin(
        PluginPaths paths,
        string pluginId,
        string packageDirectory,
        IEnumerable<FileScope> granted,
        bool includeSensitiveFiles = false) =>
        new(
        [
            new FileScope(FileScopeKind.Plugin, packageDirectory, FileAccessKind.Read),
            new FileScope(FileScopeKind.Data, paths.FilesDirectoryOf(pluginId), FileAccessKind.ReadWrite),
            new FileScope(FileScopeKind.Tmp, paths.TempDirectoryOf(pluginId), FileAccessKind.ReadWrite),
            .. granted,
        ], includeSensitiveFiles);

    /// <summary>The scopes with resolved roots and compiled globs, in the order the policy prefers them.</summary>
    internal IReadOnlyList<CompiledScope> Compiled(bool ignoreCase)
    {
        var slot = ignoreCase ? 1 : 0;
        return _compiled[slot] ??= [.. Scopes
            .Select(scope => new CompiledScope(
                scope,
                PolicyContext.Resolve(scope.Root),
                [.. scope.Include.Select(g => new PathGlob(g, ignoreCase))],
                [.. scope.Exclude.Select(g => new PathGlob(g, ignoreCase))]))
            .OrderBy(c => Preference(c.Scope.Kind))];
    }

    /// <summary>
    /// When several scopes contain a path, the first that allows it wins. The plugin's own directories come first and
    /// broad grants last, so an access is attributed to the least audited scope that allows it (design/plugins/04 §7).
    /// </summary>
    private static int Preference(FileScopeKind kind) => kind switch
    {
        FileScopeKind.Data => 0,
        FileScopeKind.Tmp => 1,
        FileScopeKind.Plugin => 2,
        FileScopeKind.Document => 3,
        FileScopeKind.Project => 4,
        FileScopeKind.Picked => 5,
        FileScopeKind.AgentData => 6,
        _ => 7,
    };

    internal sealed record CompiledScope(FileScope Scope, string RealRoot, PathGlob[] Include, PathGlob[] Exclude);
}
