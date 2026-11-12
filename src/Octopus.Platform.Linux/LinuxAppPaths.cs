namespace Octopus.Platform.Linux;

/// <summary>XDG base directories; relative XDG values are ignored as the spec requires.</summary>
public sealed class LinuxAppPaths : IAppPaths
{
    public LinuxAppPaths(string? home = null, string? xdgConfigHome = null, string? xdgCacheHome = null)
    {
        home ??= Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        xdgConfigHome ??= Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        xdgCacheHome ??= Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        _data = Path.Combine(Absolute(xdgConfigHome) ?? Path.Combine(home, ".config"), "octopus");
        _cache = Path.Combine(Absolute(xdgCacheHome) ?? Path.Combine(home, ".cache"), "octopus");
    }

    private readonly string _data;
    private readonly string _cache;

    /// <summary>Created on first access.</summary>
    public string DataDirectory => Directory.CreateDirectory(_data).FullName;

    public string CacheDirectory => Directory.CreateDirectory(_cache).FullName;

    private static string? Absolute(string? path) => !string.IsNullOrEmpty(path) && Path.IsPathRooted(path) ? path : null;
}
