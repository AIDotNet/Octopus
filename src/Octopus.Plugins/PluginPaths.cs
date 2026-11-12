using Octopus.Platform;

namespace Octopus.Plugins;

/// <summary>Where plugins and their data live, under the app's data and cache directories (design/plugins/03 §5).</summary>
public sealed class PluginPaths(IAppPaths app)
{
    /// <summary><c>&lt;data&gt;/plugins/&lt;id&gt;/</c>: installed packages, read-only for the plugins themselves.</summary>
    public string PluginsDirectory => Path.Combine(app.DataDirectory, "plugins");

    /// <summary>Packages being unpacked; on the same volume as <see cref="PluginsDirectory"/> so the final move is a rename.</summary>
    public string StagingDirectory => Path.Combine(PluginsDirectory, ".staging");

    public string PackageDirectoryOf(string id) => Path.Combine(PluginsDirectory, id);

    /// <summary>
    /// The plugin's private directory: its state files, log and audit log (the host's), and <see cref="FilesDirectoryOf"/>.
    /// </summary>
    public string DataDirectoryOf(string id) => Path.Combine(app.DataDirectory, "plugin-data", id);

    /// <summary>
    /// The plugin's <c>data</c> file scope (<c>ctx.dataPath</c>): <c>plugin-data/&lt;id&gt;/files</c>, so the plugin cannot
    /// reach the host's files next to it (design/plugins/03-清单与生命周期.md §5).
    /// </summary>
    public string FilesDirectoryOf(string id) => Path.Combine(DataDirectoryOf(id), "files");

    /// <summary>The plugin's temporary directory (its <c>tmp</c> file scope), emptied when the app exits.</summary>
    public string TempDirectoryOf(string id) => Path.Combine(app.CacheDirectory, "plugin-tmp", id);
}
