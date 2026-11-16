namespace Octopus.Platform;

/// <summary>
/// Everything under one given folder instead of the platform's own places: for running a second Octopus beside the
/// person's own (development, and two instances talking to each other on one machine). The cache goes inside it.
/// </summary>
public sealed class FixedAppPaths(string root) : IAppPaths
{
    /// <summary>The environment variable that names the folder; unset or empty means the platform's own places.</summary>
    public const string Variable = "OCTOPUS_DATA_DIR";

    private readonly string _root = Path.GetFullPath(root);

    /// <summary>The paths the variable asks for, or null when it is not set.</summary>
    public static FixedAppPaths? FromEnvironment() =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } folder ? new FixedAppPaths(folder) : null;

    /// <summary>Created on first access.</summary>
    public string DataDirectory => Directory.CreateDirectory(_root).FullName;

    public string CacheDirectory => Directory.CreateDirectory(Path.Combine(_root, "cache")).FullName;
}
