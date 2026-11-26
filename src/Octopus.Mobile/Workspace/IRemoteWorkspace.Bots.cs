namespace Octopus.Mobile.Workspace;

public partial interface IRemoteWorkspace
{
    /// <summary>True when <paramref name="host"/> supports the <c>bots</c> capability.</summary>
    bool SupportsBots(RemoteHost host) => false;

    Task<RemoteBot> CreateBotAsync(RemoteHost host, CreateRemoteBotRequest request, CancellationToken cancellationToken = default) =>
        throw new IOException("Bots are not supported on this host.");

    Task<RemoteBot> ImportBotAsync(RemoteHost host, string shareLink, CancellationToken cancellationToken = default) =>
        throw new IOException("Bots are not supported on this host.");

    Task<RemoteBot> UpdateBotAsync(
        RemoteBot bot,
        bool? isPaused = null,
        bool? lookAroundEnabled = null,
        bool? tidyUpMemoryEnabled = null,
        string? agentId = null,
        string? name = null,
        CancellationToken cancellationToken = default) =>
        throw new IOException("Bots are not supported on this host.");

    Task DeleteBotAsync(RemoteBot bot, CancellationToken cancellationToken = default) =>
        throw new IOException("Bots are not supported on this host.");

    Task<RemoteBotChat> CreateBotChatAsync(RemoteBot bot, CreateRemoteBotChatRequest request, CancellationToken cancellationToken = default) =>
        throw new IOException("Bots are not supported on this host.");

    Task<RemoteBotChat> OpenBotChatAsync(RemoteBotChat chat, CancellationToken cancellationToken = default) =>
        throw new IOException("Bots are not supported on this host.");

    Task CloseBotChatAsync(RemoteBotChat chat, CancellationToken cancellationToken = default) =>
        throw new IOException("Bots are not supported on this host.");

    Task RenameBotChatAsync(RemoteBotChat chat, string? title, CancellationToken cancellationToken = default) =>
        throw new IOException("Bots are not supported on this host.");

    Task DeleteBotChatAsync(RemoteBotChat chat, CancellationToken cancellationToken = default) =>
        throw new IOException("Bots are not supported on this host.");

    Task ResolveBotWorkItemAsync(RemoteBot bot, RemoteBotWorkItem item, string? chosenOption = null, CancellationToken cancellationToken = default) =>
        throw new IOException("Bots are not supported on this host.");

    Task ToggleBotConnectorAsync(RemoteBot bot, RemoteBotConnector connector, bool enabled, CancellationToken cancellationToken = default) =>
        throw new IOException("Bots are not supported on this host.");
}
