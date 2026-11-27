using Octopus.Core.Agents;
using Octopus.Core.Bots;
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

/// <summary>
/// Reads and writes <c>accounts.json</c>: non-secret metadata for configured agent subscription accounts
/// (design/13-多账户切换.md §4.2).
/// </summary>
public interface IAccountStore
{
    AccountsDocument Current { get; }
    event EventHandler? Changed;

    void Load();

    /// <summary>Changes <see cref="Current"/>, normalizes it, and writes the file.</summary>
    void Update(Action<AccountsDocument> mutate);
}

/// <summary>Reads and writes <c>bots.json</c>: persisted Bots, their chats, connectors, and work items.</summary>
public interface IBotStore
{
    BotsDocument Current { get; }
    event EventHandler? Changed;

    void Load();

    /// <summary>Changes <see cref="Current"/>, normalizes it, and schedules writing the file.</summary>
    void Update(Action<BotsDocument> mutate);

    /// <summary>Writes a change that is still waiting to be written, before returning (the end of the app).</summary>
    void Flush();
}

/// <summary>
/// Reads and writes <c>bot-groups.json</c> (group definitions) and <c>bot-groups/&lt;groupId&gt;.json</c>
/// (per-group message transcript and pending deliveries).
/// </summary>
public interface IBotGroupStore
{
    BotGroupsDocument Current { get; }
    event EventHandler? Changed;

    void Load();

    /// <summary>Changes <see cref="Current"/>, normalizes it, and writes <c>bot-groups.json</c>.</summary>
    void Update(Action<BotGroupsDocument> mutate);

    /// <summary>Reads a group's persisted transcript and pending delivery document (creating an empty one when missing).</summary>
    BotGroupRoomDocument ReadRoom(string groupId);

    /// <summary>Atomically updates and persists one group's room document.</summary>
    BotGroupRoomDocument UpdateRoom(string groupId, Action<BotGroupRoomDocument> mutate);

    /// <summary>Removes a group's persisted transcript file when the group is deleted.</summary>
    void DeleteRoom(string groupId);
}
