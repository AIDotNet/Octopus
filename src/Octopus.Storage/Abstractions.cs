using Octopus.Core.Plugins;
using Octopus.Core.Projects;
using Octopus.Core.Settings;

namespace Octopus.Storage;

/// <summary>Reads and writes projects.json. See design/04-数据模型与持久化.md.</summary>
public interface IProjectStore
{
    /// <summary>The saved projects; an empty document when there is no file. A damaged file is set aside, not deleted.</summary>
    Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken = default);
}

/// <summary>Reads and writes settings.json and holds the settings of this run.</summary>
public interface ISettingsStore
{
    AppSettingsModel Current { get; }
    /// <summary>Raised after <see cref="Update"/> or <see cref="Load"/> changed <see cref="Current"/>.</summary>
    event EventHandler? Changed;

    /// <summary>Reads the file (synchronously: the theme of the first frame needs it). Defaults when missing or damaged.</summary>
    void Load();

    /// <summary>Changes <see cref="Current"/> and raises <see cref="Changed"/> at once; the file is written shortly after.</summary>
    void Update(Action<AppSettingsModel> mutate);

    /// <summary>Writes a change that is still waiting to be written, before returning (the end of the app).</summary>
    void Flush();
}

/// <summary>
/// Reads and writes plugins.json: installed plugins, whether they are enabled, what they were granted
/// (design/plugins/03-清单与生命周期.md §6). A damaged file gives an empty document, so nothing stays granted by accident.
/// </summary>
public interface IPluginStateStore
{
    PluginsDocument Current { get; }
    /// <summary>Raised after <see cref="Update"/> or <see cref="Load"/> changed <see cref="Current"/>.</summary>
    event EventHandler? Changed;

    void Load();

    /// <summary>Changes <see cref="Current"/> and writes the file.</summary>
    void Update(Action<PluginsDocument> mutate);
}
