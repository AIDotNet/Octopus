using Octopus.Core.Plugins;
using Octopus.FileSystem;
using Octopus.Plugins.Manifests;

namespace Octopus.Plugins.Security;

/// <summary>
/// Decides whether a plugin may read or write a path. The only place file permissions are checked
/// (design/plugins/04 §3.4); everything that touches a file on a plugin's behalf asks here first and then works on
/// <see cref="AccessDecision.RealPath"/>. A pure function of its inputs and the file system's links: no UI, no state.
/// </summary>
public interface IPluginAccessPolicy
{
    /// <param name="grants">What the plugin holds.</param>
    /// <param name="path">The absolute path the plugin passed.</param>
    /// <param name="access"><see cref="FileAccessKind.Read"/> for anything that only looks; <see cref="FileAccessKind.ReadWrite"/> for anything that changes.</param>
    /// <param name="context">Machine-wide protection lists and path rules.</param>
    /// <param name="isDirectory">
    /// Whether the target is (or is about to become) a directory; looked up on disk when null. A directory passes a
    /// scope's include globs when something inside it could match, so a plugin can walk down to the files it may see.
    /// </param>
    AccessDecision Check(PluginGrantSet grants, string path, FileAccessKind access, PolicyContext context, bool? isDirectory = null);
}

/// <summary>
/// The steps of design/plugins/04 §3.4, in order: refuse malformed paths; normalize; resolve links (for a path that
/// does not exist yet, the deepest existing ancestor) so a link inside a project cannot carry a scope elsewhere; find
/// the scopes that contain the real path, comparing by the platform's case rules; apply the protection rules; apply the
/// scope's include and exclude globs. Rules that hold whatever the scope (credentials, other plugins, system
/// locations) are applied before looking at scopes.
/// </summary>
/// <remarks>
/// Between this check and the operation another process could swap a path component for a link. The plugin API offers
/// no way to create links, so a plugin cannot set that up itself; the window is the residual risk the design accepts
/// for an in-process host.
/// </remarks>
public sealed class PluginAccessPolicy : IPluginAccessPolicy
{
    public AccessDecision Check(PluginGrantSet grants, string path, FileAccessKind access, PolicyContext context, bool? isDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(context);
        var comparison = context.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var scopes = grants.Compiled(context.IgnoreCase);

        // 1. Forms that are refused outright.
        if (PathSyntax.Check(path, context.Windows, out var isUnc) is { } invalid)
        {
            return AccessDecision.Deny(AccessDenial.InvalidPath, invalid, null);
        }
        if (isUnc && !scopes.Any(s => s.Scope.Kind == FileScopeKind.External && IsUnc(s.Scope.Root)))
        {
            return AccessDecision.Deny(AccessDenial.InvalidPath, "Network (UNC) paths are only allowed when an external scope lists them.", null);
        }

        // 2. and 3. Lexical normalization, then the real location.
        string real;
        try
        {
            real = Path.TrimEndingDirectorySeparator(PackagePath.RealPath(Path.GetFullPath(path)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return AccessDecision.Deny(AccessDenial.InvalidPath, "The path cannot be resolved.", null);
        }

        var write = access == FileAccessKind.ReadWrite;
        var resolved = context.Real;

        // 5. (the part that does not depend on the scope) Protection that no grant overrides.
        if (IsCredential(real, scopes, resolved, comparison))
        {
            return AccessDecision.Deny(AccessDenial.Credential, "Another application's login credentials are never accessible to plugins.", real);
        }
        if (InsideAny(real, resolved.OtherPlugins, comparison))
        {
            return AccessDecision.Deny(AccessDenial.Protected, "Another plugin's directory is protected.", real);
        }
        if (write && InsideAny(real, resolved.System, comparison))
        {
            return AccessDecision.Deny(AccessDenial.SystemLocation, "System locations are never writable.", real);
        }

        // 4. The scopes that contain the path, each with 5. and 6.; the first that allows wins.
        var directory = isDirectory ?? Directory.Exists(real);
        AccessDecision? refusal = null;
        foreach (var scope in scopes)
        {
            if (!InsideOrEqual(scope.RealRoot, real, comparison)) continue;
            var decision = Evaluate(scope, real, write, directory, grants, resolved, comparison);
            if (decision.Allowed) return decision;
            if (refusal is null || Severity(decision.Denial!.Value) < Severity(refusal.Denial!.Value)) refusal = decision;
        }
        return refusal ?? AccessDecision.Deny(AccessDenial.NotInScope, "The path is not inside any file scope the plugin holds.", real);
    }

    private static AccessDecision Evaluate(
        PluginGrantSet.CompiledScope compiled,
        string real,
        bool write,
        bool isDirectory,
        PluginGrantSet grants,
        PolicyContext.Resolved resolved,
        StringComparison comparison)
    {
        var scope = compiled.Scope;
        var ownDirectory = scope.Kind is FileScopeKind.Plugin or FileScopeKind.Data or FileScopeKind.Tmp;
        var relative = Relative(compiled.RealRoot, real);

        // Octopus's own data: settings.json may hold agent API keys, plugins.json the grants. Only the plugin's own
        // package, data and temporary directories (which live in there) are reachable.
        if (!ownDirectory && InsideAny(real, resolved.Protected, comparison))
        {
            return AccessDecision.Deny(AccessDenial.Protected, "Octopus's data directory is protected.", real);
        }
        // .git is not part of a project scope (git goes through octopus.git), and nothing writes into any .git.
        if (scope.Kind == FileScopeKind.Project && HasSegment(relative, ".git", comparison))
        {
            return AccessDecision.Deny(AccessDenial.Protected, ".git is not part of the project scope; use octopus.git.", real);
        }
        if (write && HasSegment(real, ".git", comparison))
        {
            return AccessDecision.Deny(AccessDenial.Protected, "Writing into .git is not allowed.", real);
        }
        if (write && !scope.AllowsWrite)
        {
            return AccessDecision.Deny(AccessDenial.ReadOnlyScope, $"The {Name(scope.Kind)} scope is read-only.", real);
        }
        // The person pointed at a document or picked file themselves; the plugin's own directories hold its own files.
        var sensitiveApplies = !grants.IncludeSensitiveFiles && !ownDirectory && scope.Kind is not (FileScopeKind.Document or FileScopeKind.Picked);
        if (sensitiveApplies && SensitiveFiles.IsSensitive(real))
        {
            return AccessDecision.Deny(AccessDenial.Sensitive, "Sensitive files need \"include sensitive files\" to be turned on for the plugin.", real);
        }
        if (relative.Length > 0)
        {
            if (compiled.Exclude.Any(glob => MatchesSelfOrParent(glob, relative)))
            {
                return AccessDecision.Deny(AccessDenial.NotInScope, $"Excluded by the {Name(scope.Kind)} scope's exclude patterns.", real);
            }
            var included = compiled.Include.Length == 0 ||
                compiled.Include.Any(glob => MatchesSelfOrParent(glob, relative) || (isDirectory && glob.CouldMatchInside(relative)));
            if (!included)
            {
                return AccessDecision.Deny(AccessDenial.NotInScope, $"Not matched by the {Name(scope.Kind)} scope's include patterns.", real);
            }
        }
        return AccessDecision.Allow(scope, real);
    }

    /// <summary>
    /// Credentials by name (<see cref="SensitiveFiles.IsCredentialByName"/>), and the credential files directly inside
    /// an agent's data directory: every <c>agentData</c> scope root and every directory the host listed, with the
    /// generic names and each definition's own.
    /// </summary>
    private static bool IsCredential(
        string real,
        IReadOnlyList<PluginGrantSet.CompiledScope> scopes,
        PolicyContext.Resolved resolved,
        StringComparison comparison)
    {
        if (SensitiveFiles.IsCredentialByName(real)) return true;
        foreach (var scope in scopes)
        {
            if (scope.Scope.Kind != FileScopeKind.AgentData) continue;
            foreach (var name in SensitiveFiles.AgentCredentialNames)
            {
                if (SamePath(real, Path.Combine(scope.RealRoot, name))) return true;
            }
        }
        foreach (var agent in resolved.Agents)
        {
            foreach (var name in SensitiveFiles.AgentCredentialNames.Concat(agent.Files))
            {
                if (SamePath(real, Path.Combine(agent.DataDirectory, name.Replace('/', Path.DirectorySeparatorChar)))) return true;
            }
        }
        return false;

        // Credential names are compared ignoring case everywhere: refusing too much here is the safe side.
        static bool SamePath(string a, string b) => string.Equals(a, Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rank of a refusal when several scopes refused: the one that explains the most wins.</summary>
    private static int Severity(AccessDenial denial) => denial switch
    {
        AccessDenial.Credential => 0,
        AccessDenial.Protected => 1,
        AccessDenial.SystemLocation => 2,
        AccessDenial.Sensitive => 3,
        AccessDenial.ReadOnlyScope => 4,
        _ => 5,
    };

    /// <summary>A path is in or out with its parents: excluding <c>node_modules</c> excludes everything below it.</summary>
    private static bool MatchesSelfOrParent(PathGlob glob, string relative)
    {
        for (var end = relative.IndexOf('/'); end >= 0; end = relative.IndexOf('/', end + 1))
        {
            if (glob.IsMatch(relative[..end])) return true;
        }
        return glob.IsMatch(relative);
    }

    private static string Relative(string root, string path)
    {
        if (path.Length <= root.Length) return "";
        var rest = path[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.DirectorySeparatorChar == '/' ? rest : rest.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool HasSegment(string path, string name, StringComparison comparison)
    {
        foreach (var segment in path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(segment, name, comparison)) return true;
        }
        return false;
    }

    internal static bool InsideOrEqual(string root, string path, StringComparison comparison)
    {
        if (string.Equals(root, path, comparison)) return true;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }

    private static bool InsideAny(string path, IReadOnlyList<string> roots, StringComparison comparison)
    {
        foreach (var root in roots)
        {
            if (InsideOrEqual(root, path, comparison)) return true;
        }
        return false;
    }

    private static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    private static string Name(FileScopeKind kind) => kind switch
    {
        FileScopeKind.AgentData => "agentData",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
