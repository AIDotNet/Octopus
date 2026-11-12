namespace Octopus.Platform.MacOS;

/// <summary>Application Support and Caches under the user's Library, as macOS apps do.</summary>
public sealed class MacAppPaths : IAppPaths
{
    public MacAppPaths(string? home = null)
    {
        home ??= Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _data = Path.Combine(home, "Library", "Application Support", "Octopus");
        _cache = Path.Combine(home, "Library", "Caches", "Octopus");
    }

    private readonly string _data;
    private readonly string _cache;

    /// <summary>Created on first access.</summary>
    public string DataDirectory => Directory.CreateDirectory(_data).FullName;

    public string CacheDirectory => Directory.CreateDirectory(_cache).FullName;
}
