using Octopus.FileSystem;
using Octopus.Platform;
using Octopus.Plugins.Manifests;

namespace Octopus.Plugins.Security;

/// <summary>An agent's data directory as the host knows it, with the credential files its definition lists (relative).</summary>
public sealed record AgentCredentialLocation(string DataDirectory, IReadOnlyList<string> Files);

/// <summary>
/// The machine-wide facts the access policy needs besides the plugin's grants: what Octopus protects, where other
/// plugins and agents live, and the platform's path rules. One instance serves every plugin; build a new one when
/// one of these lists changes (the resolved paths are cached).
/// </summary>
public sealed class PolicyContext
{
    private Resolved? _resolved;

    /// <summary>
    /// Octopus's own directories (data and cache: settings.json may hold agent API keys, plugins.json the grants).
    /// Nothing inside is reachable except through the plugin's own <c>plugin</c>, <c>data</c> and <c>tmp</c> scopes.
    /// </summary>
    public IReadOnlyList<string> ProtectedDirectories { get; init; } = [];

    /// <summary>Package and data directories of other plugins outside <see cref="ProtectedDirectories"/> (built-in, development).</summary>
    public IReadOnlyList<string> OtherPluginDirectories { get; init; } = [];

    /// <summary>Agent data directories the host resolved, with each definition's credential files.</summary>
    public IReadOnlyList<AgentCredentialLocation> AgentCredentials { get; init; } = [];

    /// <summary>Never writable (<see cref="SensitiveFiles.DefaultSystemLocations"/> by default).</summary>
    public IReadOnlyList<string> SystemLocations { get; init; } = SensitiveFiles.DefaultSystemLocations();

    /// <summary>Paths compare ignoring case (macOS, Windows) or not (Linux).</summary>
    public bool IgnoreCase { get; init; } = PathGlob.PlatformIgnoresCase;

    /// <summary>Windows path rules apply: device paths, streams and reserved names are refused, UNC needs an external scope.</summary>
    public bool Windows { get; init; } = OperatingSystem.IsWindows();

    /// <summary>
    /// The usual context: Octopus's data and cache directories protected, and the running installation of Octopus
    /// (<see cref="AppContext.BaseDirectory"/>) never writable, since a plugin that could write there could change
    /// Octopus itself.
    /// </summary>
    public static PolicyContext For(
        IAppPaths app,
        IEnumerable<string>? otherPluginDirectories = null,
        IEnumerable<AgentCredentialLocation>? agentCredentials = null) => new()
    {
        ProtectedDirectories = [app.DataDirectory, app.CacheDirectory],
        OtherPluginDirectories = [.. otherPluginDirectories ?? []],
        AgentCredentials = [.. agentCredentials ?? []],
        SystemLocations = [.. SensitiveFiles.DefaultSystemLocations(), AppContext.BaseDirectory],
    };

    internal Resolved Real => _resolved ??= new Resolved(
        [.. ProtectedDirectories.Select(Resolve)],
        [.. OtherPluginDirectories.Select(Resolve)],
        [.. AgentCredentials.Select(a => new AgentCredentialLocation(Resolve(a.DataDirectory), a.Files))],
        [.. SystemLocations.Select(Resolve)]);

    /// <summary>A configured path with links resolved, so it compares with the resolved paths being checked.</summary>
    internal static string Resolve(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(PackagePath.RealPath(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
    }

    internal sealed record Resolved(
        IReadOnlyList<string> Protected,
        IReadOnlyList<string> OtherPlugins,
        IReadOnlyList<AgentCredentialLocation> Agents,
        IReadOnlyList<string> System);
}
