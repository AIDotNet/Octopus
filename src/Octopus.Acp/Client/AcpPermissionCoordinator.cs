using System.Text.Json.Nodes;
using Octopus.Acp.Protocol;
using Octopus.Core.Agents;

namespace Octopus.Acp.Client;

/// <summary>
/// Decides whether an incoming <c>session/request_permission</c> can be auto-approved under the current permission
/// mode or must be shown to the user. Modeled after Orca's <c>acp-structured-permission-handler.ts</c>:
/// any request flagged as requiring manual approval (questions, decisions, security warnings) is NEVER auto-approved.
/// </summary>
public static class AcpPermissionCoordinator
{
    public static string? TryAutoSelectOptionId(
        AcpPermissionRequest request,
        string activePermissionMode,
        AcpPermissionStrategy strategy,
        bool requireManualApproval)
    {
        if (requireManualApproval)
        {
            return null;
        }
        // Honor explicit full-access even for AgentMode agents if they still send a tool permission request
        // (e.g. switched mid-session or agent has no native bypass mode). Manual questions/warnings are blocked above.
        if (activePermissionMode == PermissionModes.Full)
        {
            return FindAllowOptionId(request.Options);
        }
        if (activePermissionMode == PermissionModes.AcceptEdits)
        {
            var kind = request.ToolCall.Kind;
            if (kind is "read" or "search" or "edit" or "move" or "think")
            {
                return FindAllowOptionId(request.Options);
            }
        }
        if (activePermissionMode == PermissionModes.DontAsk)
        {
            return FindRejectOptionId(request.Options);
        }
        return null;
    }

    public static JsonNode BuildSelectedReply(string optionId) => new JsonObject
    {
        ["outcome"] = new JsonObject
        {
            ["outcome"] = "selected",
            ["optionId"] = optionId,
        },
    };

    public static JsonNode BuildCancelledReply() => new JsonObject
    {
        ["outcome"] = new JsonObject
        {
            ["outcome"] = "cancelled",
        },
    };

    private static string? FindAllowOptionId(IReadOnlyList<AcpPermissionOption> options) =>
        options.FirstOrDefault(o => o.Kind == "allow_once")?.OptionId
        ?? options.FirstOrDefault(o => o.Kind == "allow_always")?.OptionId;

    private static string? FindRejectOptionId(IReadOnlyList<AcpPermissionOption> options) =>
        options.FirstOrDefault(o => o.Kind == "reject_once")?.OptionId
        ?? options.FirstOrDefault(o => o.Kind == "reject_always")?.OptionId;
}
