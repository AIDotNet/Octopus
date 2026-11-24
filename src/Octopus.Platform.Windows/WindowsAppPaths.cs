namespace Octopus.Platform.Windows;

/// <summary>Data lives under <c>%UserProfile%\.octopus</c>; disposable caches stay under <c>%LocalAppData%\Octopus\Cache</c>.</summary>
public sealed class WindowsAppPaths : IAppPaths
{
    private readonly Lock _migrationGate = new();
    private readonly string _data;
    private readonly string _legacyData;
    private readonly string _cache;
    private bool _migrated;

    public WindowsAppPaths(string? home = null, string? appData = null, string? localAppData = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        appData ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _data = Path.Combine(home, ".octopus");
        _legacyData = string.IsNullOrWhiteSpace(appData) ? "" : Path.Combine(appData, "Octopus");
        _cache = Path.Combine(localAppData, "Octopus", "Cache");
    }

    /// <summary>Created on first access; migrates legacy %AppData%\Octopus data once if present.</summary>
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
