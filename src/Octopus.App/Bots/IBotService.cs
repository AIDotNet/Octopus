using Octopus.Core.Agents;
using Octopus.Core.Bots;
using Octopus.Core.Projects;

namespace Octopus.App.Bots;

public enum BotChangeKind
{
    Mode,
    Bots,
    CurrentBot,
    Chats,
    ActiveChat,
    Connectors,
    Items,
    Files,
    Schedules
}

public sealed record BotChangedEventArgs(BotChangeKind Kind, BotDefinition? Bot = null, BotChat? Chat = null, ProjectItem? Item = null);

public sealed record NewBotRequest(
    string Name,
    string AgentId,
    string Color,
    string? TemplateId,
    string? Soul,
    string HostDeviceKey = "local",
    string? Image = null,
    string? Model = null);

/// <summary>
/// Manages Bots, Bot mode switching, home directories, chats, connectors, and backing projects for the workspace dock.
/// </summary>
public interface IBotService
{
    IReadOnlyList<BotDefinition> Bots { get; }
    bool IsBotMode { get; }
    BotDefinition? CurrentBot { get; }
    BotChat? CurrentChat { get; }
    string? ActiveGroupId { get; }
    string? ActiveGroupThreadId { get; }
    BotHomeManager Home { get; }
    BotConnectorRuntime ConnectorRuntime { get; }

    event EventHandler<BotChangedEventArgs>? Changed;

    void Load();
    void SetBotMode(bool enabled);
    void ToggleBotMode();

    BotDefinition CreateBot(NewBotRequest request);
    void SelectBot(string botId);
    void SelectGroup(string groupId, string? threadId = null);
    void ClearActiveGroup();
    void SetBotExpanded(string botId, bool expanded);
    void RenameBot(string botId, string name);
    void SetBotPaused(string botId, bool paused);
    void SetBotUnread(string botId, bool unread);
    void SetBotAppearance(string botId, string color, string? icon, string? image);
    void UpdateBotSettings(string botId, Action<BotDefinition> mutate);
    void DeleteBot(string botId);

    BotChat? CreateChat(string botId, string? agentId = null, string? title = null, bool activate = true);
    /// <summary>Creates an isolated background chat owned by a bot inside a Bot Group room or thread.</summary>
    BotChat? CreateGroupMemberChat(string botId, string groupId, string? threadId, string title);
    /// <summary>Returns a <see cref="ProjectItem"/> for a group member chat without inserting it into the single-bot dock.</summary>
    ProjectItem? GetGroupMemberBackingItem(string botId, string chatId);
    void OpenChat(string botId, string chatId);
    void CloseChat(string botId, string chatId);
    void RenameChat(string botId, string chatId, string? customTitle);
    void DeleteChat(string botId, string chatId);

    BotConnectorBinding? AddConnector(string botId, string appId);
    Task<BotConnectorBinding?> AddConnectorWithConfigAsync(string botId, string appId, string? variableValue = null, string? bearerToken = null, string? urlOverride = null, CancellationToken ct = default);
    BotConnectorBinding? AddCustomUrlConnector(string botId, string name, string url);
    Task<BotConnectorBinding?> AddCustomUrlConnectorAsync(string botId, string name, string url, string transport = "http", string? bearerToken = null, CancellationToken ct = default);
    Task<BotConnectorBinding?> ConfigureConnectorAsync(string botId, string connectorId, string? url, string transport, string? variableValue, string? bearerToken, bool clearToken = false, CancellationToken ct = default);
    Task<BotMcpOAuthResult> SignInConnectorOAuthAsync(string botId, string connectorId, string? urlOverride = null, CancellationToken ct = default);
    void SetConnectorEnabled(string botId, string connectorId, bool enabled);
    void RemoveConnector(string botId, string connectorId);
    Task<IReadOnlyList<AcpMcpServerConfig>> BuildAcpMcpServersAsync(string botId, CancellationToken ct = default);

    BotDefinition ImportBot(BotSharePayload payload, string hostDeviceKey = "local");

    /// <summary>
    /// Changes the bot's schedules in one saved step. <paramref name="mutate"/> returns false when it changed nothing.
    /// Raises <see cref="BotChangeKind.Schedules"/> without rewriting the bot's home files.
    /// </summary>
    bool MutateSchedules(string botId, Func<List<BotSchedule>, bool> mutate);
    BotDefinition? FindBotByChatId(string chatId);
    /// <summary>
    /// Makes sure the chat is open as an item of the bot's backing project without switching to it, so a scheduled run
    /// can be delivered while the owner works elsewhere. Returns the backing item, or null when the chat is unknown.
    /// </summary>
    ProjectItem? EnsureChatOpenInBackground(string botId, string chatId);

    BotWorkItem? AddWorkItem(string botId, string title, BotWorkItemStatus status = BotWorkItemStatus.Planned, string? question = null, IReadOnlyList<string>? options = null, string? chatId = null);
    void ResolveWorkItem(string botId, string itemId, string? chosenOption = null);

    /// <summary>Opens a file from a bot's home directory as a tab inside that bot's center dock.</summary>
    ProjectItem? OpenFile(string botId, string filePath);

    /// <summary>Returns the synthetic backing <see cref="Project"/> used to host a bot's tabs in <c>WorkspaceViewModel</c>.</summary>
    Project GetBackingProject(BotDefinition bot);
    BotDefinition? FindBotByBackingProjectId(string projectId);
    ProjectItem? FindItem(string itemId);
    void ActivateBackingItem(string botId, string itemId);
    void CloseBackingItem(string botId, string itemId);
    void SetDockLayout(string botId, string? layout);

    string GetHomePath(BotDefinition bot);
    int TotalNeedsAttentionCount();
    bool HasWorkingBots();
}
