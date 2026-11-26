using System.Text.Json;
using System.Text.Json.Nodes;
using Octopus.Acp.Protocol;
using Octopus.Acp.Transport;

namespace Octopus.Acp.Dialects;

/// <summary>Result of a dialect handling a custom agent→client request (either interactive question or immediate settlement).</summary>
public sealed class AcpDialectRequestHandling
{
    /// <summary>Immediate reply to send back without prompting the user (e.g. <c>cursor/create_plan</c>, <c>craft/drainMidTurnQueue</c>).</summary>
    public JsonNode? ImmediateReply { get; init; }
    /// <summary>Optional markdown plan emitted alongside an immediate settlement.</summary>
    public string? PlanMarkdown { get; init; }
    /// <summary>Optional normalized tool call update emitted alongside an immediate settlement (e.g. <c>cursor/generate_image</c>).</summary>
    public AcpToolCallUpdate? ToolCallUpdate { get; init; }
    /// <summary>Interactive question card shown to the user (never auto-approved even in full-access mode).</summary>
    public AcpQuestionRequest? Question { get; init; }
    /// <summary>Builds the JSON-RPC response payload from the user's selected answers (null means cancelled).</summary>
    public Func<IReadOnlyDictionary<string, IReadOnlyList<string>>?, JsonNode>? BuildQuestionReply { get; init; }
    /// <summary>Marks a <c>session/request_permission</c> call as user-required (never auto-approved in full mode).</summary>
    public bool RequireManualApproval { get; init; }
    /// <summary>Marks a <c>session/request_permission</c> call as an interactive Plan Approval gate (e.g. Claude Code <c>ExitPlanMode</c>).</summary>
    public bool IsPlanApproval { get; init; }
    /// <summary>Optional security warning or action summary to surface on the approval card.</summary>
    public string? ApprovalNotice { get; init; }
}

/// <summary>
/// Per-agent ACP compatibility adapter modeled directly on Orca's <c>src/main/acp/acp-dialects/acp-dialect.ts</c>.
/// Keeps protocol core and GUI completely free of per-agent conditionals.
/// </summary>
public interface IAcpDialect
{
    string Id { get; }

    /// <summary>Whether to advertise <c>elicitation.form</c> during <c>initialize</c>.</summary>
    bool AdvertiseFormElicitation => false;

    /// <summary>Extra <c>clientCapabilities._meta</c> keys to send during <c>initialize</c>.</summary>
    IReadOnlyDictionary<string, JsonNode>? ExtraClientCapabilitiesMeta => null;

    /// <summary>Optional clientInfo override (e.g. older <c>agy</c> requiring <c>zed</c> to unlock full models).</summary>
    AcpImplementation? OverrideClientInfo(string? agentVersion) => null;

    /// <summary>Whether <c>@file</c> mentions must be emitted as <c>resource_link</c> prompt blocks.</summary>
    bool LinksFileMentions => false;

    /// <summary>Whether the agent advertises image support yet drops inline image blocks (pass file paths instead).</summary>
    bool DropsPromptImages => false;

    /// <summary>Allows unprompted background task/subagent notifications after <c>end_turn</c>.</summary>
    bool ReactiveWakeups => false;

    /// <summary>Slash commands to hide from the GUI because they would corrupt session state (e.g. <c>clear</c>, <c>reset</c>, <c>logout</c>).</summary>
    IReadOnlyCollection<string> WithheldCommands => [];

    /// <summary>Replacement slash command for <c>/compact</c> (e.g. <c>/compress</c> for Qwen/Hermes).</summary>
    string? CompactCommand => null;

    /// <summary>Config option id controlling fast mode (e.g. <c>fast</c> on Cursor).</summary>
    string? FastConfigId => null;

    /// <summary>Config option id controlling thinking toggle (e.g. <c>thinking</c> on Cursor).</summary>
    string? ThinkingConfigId => null;

    /// <summary>Maps CLI model aliases to ACP server model ids (e.g. Cursor <c>auto → default</c>).</summary>
    IReadOnlyDictionary<string, string>? ChatModelIdAliases => null;

    /// <summary>Maps CLI effort spellings to ACP server effort ids (e.g. Cursor <c>xhigh → extra-high</c>).</summary>
    IReadOnlyDictionary<string, string>? ChatEffortIdAliases => null;

    /// <summary>Maps Build/Plan interaction modes to native ACP mode ids.</summary>
    IReadOnlyDictionary<string, string>? InteractionModes => null;

    /// <summary>Selects a non-interactive auth method when <c>session/new</c> returns -32000 AuthRequired.</summary>
    string? SelectAuthMethod(IReadOnlyList<AcpAuthMethod> advertised, IReadOnlyDictionary<string, string> env) => null;

    /// <summary>Normalizes agent-specific <c>tool_call</c> / <c>tool_call_update</c> fields into unified Command/Stdout/Stderr/ExitCode/Diff.</summary>
    void NormalizeToolUpdate(AcpToolCallUpdate update) { }

    /// <summary>Classifies whether an advertised slash command is actually a skill and cleans its description.</summary>
    string? ExtractSkillDescription(AcpAvailableCommand command) => null;

    /// <summary>Intercepts agent-specific custom requests or special <c>session/request_permission</c> questions/warnings.</summary>
    AcpDialectRequestHandling? TryHandleRequest(string method, JsonElement? parameters, string requestId) => null;

    /// <summary>Translates a non-standard / extension notification outside <c>session/update</c> into a runtime event.</summary>
    AcpRuntimeEvent? TryTranslateNotification(string method, JsonElement? parameters) => null;

    /// <summary>Detects whether an RPC error means authentication is required.</summary>
    bool IsAuthenticationRequired(AcpRpcException error) => error.Code == -32000;

    /// <summary>Extracts a clean user-facing error detail from a prompt error when available.</summary>
    string? FormatPromptError(AcpRpcException error) => null;

    /// <summary>
    /// True when switching to a custom model id not listed by the ACP server requires reconnecting/resuming the
    /// session with updated launch environment preferences (e.g. <c>codex-acp</c>'s <c>CODEX_CONFIG</c>).
    /// </summary>
    bool ReconnectOnCustomModelSwitch => false;

    /// <summary>
    /// Applies initial launch preferences (such as a custom third-party model id or reasoning effort) to the
    /// ACP child process environment before startup.
    /// </summary>
    void ApplyLaunchPreferences(IDictionary<string, string> env, string? modelId, string? effortId) { }
}
