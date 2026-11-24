namespace Octopus.Platform.Linux;

/// <summary>Data lives under <c>~/.octopus</c>; disposable caches stay under XDG cache (<c>~/.cache/octopus</c>).</summary>
public sealed class LinuxAppPaths : IAppPaths
{
    private readonly Lock _migrationGate = new();
    private readonly string _data;
    private readonly string _legacyData;
    private readonly string _cache;
    private bool _migrated;

    public LinuxAppPaths(string? home = null, string? xdgConfigHome = null, string? xdgCacheHome = null)
    {
        home ??= Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        xdgConfigHome ??= Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        xdgCacheHome ??= Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        _data = Path.Combine(home, ".octopus");
        _legacyData = Path.Combine(Absolute(xdgConfigHome) ?? Path.Combine(home, ".config"), "octopus");
        _cache = Path.Combine(Absolute(xdgCacheHome) ?? Path.Combine(home, ".cache"), "octopus");
    }

    /// <summary>Created on first access; migrates legacy XDG config data once if present.</summary>
    public string DataDirectory
    {
        get
        {
            if (!_migrated)
            {
                lock (_migrationGate)
                {
                    if (!_migrated)
                    {
                        LegacyDataMigration.MigrateIfNeeded(_data, _legacyData);
                        _migrated = true;
                    }
                }
            }
            return Directory.CreateDirectory(_data).FullName;
        }
    }

    public string CacheDirectory => Directory.CreateDirectory(_cache).FullName;

    private static string? Absolute(string? path) => !string.IsNullOrEmpty(path) && Path.IsPathRooted(path) ? path : null;
}
