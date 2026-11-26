namespace Octopus.Mobile.Workspace;

public partial interface IRemoteWorkspace
{
    /// <summary>True when <paramref name="host"/> supports the <c>bot.groups</c> capability.</summary>
    bool SupportsBotGroups(RemoteHost host) => false;

    Task<RemoteBotGroup> CreateBotGroupAsync(RemoteHost host, CreateRemoteBotGroupRequest request, CancellationToken cancellationToken = default) =>
        throw new IOException("Bot Groups are not supported on this host.");

    Task<RemoteBotGroup> UpdateBotGroupAsync(RemoteBotGroup group, UpdateRemoteBotGroupRequest request, CancellationToken cancellationToken = default) =>
        throw new IOException("Bot Groups are not supported on this host.");

    Task DeleteBotGroupAsync(RemoteBotGroup group, CancellationToken cancellationToken = default) =>
        throw new IOException("Bot Groups are not supported on this host.");

    Task<RemoteBotGroupRoomSnapshot> ReadBotGroupRoomAsync(
        RemoteBotGroup group,
        string? threadId = null,
        long? beforeSequence = null,
        int limit = 60,
        CancellationToken cancellationToken = default) =>
        throw new IOException("Bot Groups are not supported on this host.");

    Task<RemoteBotGroupEntry> SendBotGroupMessageAsync(
        RemoteBotGroup group,
        string text,
        string? threadId = null,
        string? messageId = null,
        CancellationToken cancellationToken = default) =>
        throw new IOException("Bot Groups are not supported on this host.");

    Task StopBotGroupAsync(
        RemoteBotGroup group,
        string? threadId = null,
        string? botId = null,
        CancellationToken cancellationToken = default) =>
        throw new IOException("Bot Groups are not supported on this host.");

    Task ContinueBotGroupHandoffAsync(
        RemoteBotGroup group,
        string messageId,
        int extraHops = 3,
        CancellationToken cancellationToken = default) =>
        throw new IOException("Bot Groups are not supported on this host.");

    Task<RemoteBotGroupThread> CreateBotGroupThreadAsync(RemoteBotGroup group, CancellationToken cancellationToken = default) =>
        throw new IOException("Bot Groups are not supported on this host.");

    Task RespondBotGroupApprovalAsync(
        RemoteBotGroup group,
        string approvalId,
        string? optionId,
        CancellationToken cancellationToken = default) =>
        throw new IOException("Bot Groups are not supported on this host.");
}
