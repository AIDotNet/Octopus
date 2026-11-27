using Octopus.Acp.Protocol;
using Octopus.Core.Bots;

namespace Octopus.App.Bots.Groups;

public enum BotGroupChangeKind
{
    Groups,
    Selection,
    Messages,
    Approvals
}

public sealed record BotGroupChangedEventArgs(
    BotGroupChangeKind Kind,
    string? GroupId = null,
    string? ThreadId = null);

public sealed record BotGroupStreamingEventArgs(
    string GroupId,
    string? ThreadId,
    string OperationId,
    string PreviewText);

public sealed record CreateBotGroupRequest(
    string Name,
    IReadOnlyList<string> BotIds,
    BotGroupReplyMode? ReplyMode = null,
    int MaxHops = BotGroupDefinition.DefaultMaxHops);

public sealed record UpdateBotGroupRequest(
    string GroupId,
    int Revision,
    string? Name = null,
    IReadOnlyList<string>? BotIds = null,
    BotGroupReplyMode? ReplyMode = null,
    int? MaxHops = null);

public sealed record BotGroupRoomPage(
    BotGroupDefinition Group,
    string? ThreadId,
    IReadOnlyList<BotGroupMessage> Messages,
    IReadOnlyList<BotGroupPendingApproval> Approvals,
    bool HasEarlier);

public sealed class BotGroupPendingApproval(
    string approvalId,
    string groupId,
    string? threadId,
    string botId,
    string botName,
    string botColor,
    string chatId,
    string title,
    string? detail,
    string? warningMessage,
    IReadOnlyList<AcpPermissionOption> options,
    Func<string?, bool> respond)
{
    public string ApprovalId { get; } = approvalId;
    public string GroupId { get; } = groupId;
    public string? ThreadId { get; } = threadId;
    public string BotId { get; } = botId;
    public string BotName { get; } = botName;
    public string BotColor { get; } = botColor;
    public string ChatId { get; } = chatId;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public string? WarningMessage { get; } = warningMessage;
    public IReadOnlyList<AcpPermissionOption> Options { get; } = options;

    public bool Respond(string? optionId) => respond(optionId);
}

/// <summary>
/// Manages multi-bot Group Rooms: membership, threads, two-phase message delivery, incremental context,
/// Stop watermarks, automatic bot-to-bot @mention handoffs, and aggregated approval docking.
/// </summary>
public interface IBotGroupService
{
    IReadOnlyList<BotGroupDefinition> Groups { get; }
    BotGroupDefinition? CurrentGroup { get; }
    string? CurrentThreadId { get; }

    event EventHandler<BotGroupChangedEventArgs>? Changed;
    event EventHandler<BotGroupStreamingEventArgs>? StreamingUpdated;

    void Load();
    Task ResumePendingDeliveriesAsync(CancellationToken cancellationToken = default);

    BotGroupDefinition? FindGroup(string groupId);
    BotGroupDefinition CreateGroup(CreateBotGroupRequest request);
    Task<BotGroupDefinition> UpdateGroupAsync(UpdateBotGroupRequest request, CancellationToken cancellationToken = default);
    Task DeleteGroupAsync(string groupId, CancellationToken cancellationToken = default);
    Task<BotGroupThread> CreateThreadAsync(string groupId, CancellationToken cancellationToken = default);
    Task DeleteThreadAsync(string groupId, string threadId, CancellationToken cancellationToken = default);

    void SelectGroup(string groupId, string? threadId = null);
    void SetGroupExpanded(string groupId, bool expanded);
    void MarkGroupRead(string groupId);

    BotGroupRoomPage ReadRoom(string groupId, string? threadId = null, long? beforeSequence = null, int limit = 60);

    Task<BotGroupMessage> SendMessageAsync(
        string groupId,
        int revision,
        string text,
        string? threadId = null,
        string? messageId = null,
        CancellationToken cancellationToken = default);

    Task StopAsync(string groupId, string? threadId = null, string? botId = null, CancellationToken cancellationToken = default);

    Task<bool> ContinueHandoffAsync(string groupId, string pausedMessageId, int extraHops = 3, CancellationToken cancellationToken = default);

    IReadOnlyList<BotGroupPendingApproval> GetPendingApprovals(string? groupId = null, string? threadId = null);
    bool RespondApproval(string approvalId, string? optionId);

    string? GetStreamingPreview(string operationId);
    int TotalPendingApprovalCount();
    bool HasWorkingGroups();
    bool IsGroupWorking(string groupId);

    /// <summary>Opens the backing member chat tab so the user can inspect the bot's detailed tool/thinking transcript.</summary>
    void OpenMemberSessionInspector(string botId, string chatId);
}
