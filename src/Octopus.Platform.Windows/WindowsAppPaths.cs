namespace Octopus.Platform.Windows;

/// <summary>%AppData%\Octopus for data, %LocalAppData%\Octopus\Cache for disposable files. Not verified on real Windows.</summary>
public sealed class WindowsAppPaths : IAppPaths
{
    public WindowsAppPaths()
    {
        _data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Octopus");
        _cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Octopus", "Cache");
    }

    private readonly string _data;
    private readonly string _cache;

    /// <summary>Created on first access.</summary>
    public string DataDirectory => Directory.CreateDirectory(_data).FullName;

    public string CacheDirectory => Directory.CreateDirectory(_cache).FullName;
}
