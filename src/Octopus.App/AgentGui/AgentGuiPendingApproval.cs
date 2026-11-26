using Octopus.Acp.Protocol;

namespace Octopus.App.AgentGui;

/// <summary>Live permission/approval request raised by an open <see cref="AgentGuiTabViewModel"/>.</summary>
public sealed class AgentGuiPendingApproval(
    string approvalId,
    string itemId,
    string title,
    string? detail,
    string? warningMessage,
    IReadOnlyList<AcpPermissionOption> options,
    Action<string?> respond)
{
    private int _resolved;

    public string ApprovalId { get; } = approvalId;
    public string ItemId { get; } = itemId;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public string? WarningMessage { get; } = warningMessage;
    public IReadOnlyList<AcpPermissionOption> Options { get; } = options;
    public bool IsResolved => Volatile.Read(ref _resolved) != 0;

    public bool TryRespond(string? optionId)
    {
        if (Interlocked.Exchange(ref _resolved, 1) != 0) return false;
        respond(optionId);
        return true;
    }
}
