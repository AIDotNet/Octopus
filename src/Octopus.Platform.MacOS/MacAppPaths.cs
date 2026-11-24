namespace Octopus.Platform.MacOS;

/// <summary>Data lives under <c>~/.octopus</c>; disposable caches stay under <c>~/Library/Caches/Octopus</c>.</summary>
public sealed class MacAppPaths : IAppPaths
{
    private readonly Lock _migrationGate = new();
    private readonly string _data;
    private readonly string _legacyData;
    private readonly string _cache;
    private bool _migrated;

    public MacAppPaths(string? home = null)
    {
        home ??= Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _data = Path.Combine(home, ".octopus");
        _legacyData = Path.Combine(home, "Library", "Application Support", "Octopus");
        _cache = Path.Combine(home, "Library", "Caches", "Octopus");
    }

    /// <summary>Created on first access; migrates legacy Application Support data once if present.</summary>
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
}
