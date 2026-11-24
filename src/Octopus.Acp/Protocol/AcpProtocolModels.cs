using System.Text.Json;
using System.Text.Json.Serialization;

namespace Octopus.Acp.Protocol;

/// <summary>ACP protocol constants (schema-v1.21.0).</summary>
public static class AcpProtocolConstants
{
    public const int ProtocolVersion = 1;
}

public sealed class AcpImplementation
{
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public string Version { get; set; } = "";
}

public sealed class AcpPromptCapabilities
{
    public bool Image { get; set; }
    public bool Audio { get; set; }
    public bool EmbeddedContext { get; set; }
}

public sealed class AcpSessionCapabilities
{
    public bool SupportsList { get; set; }
    public bool SupportsDelete { get; set; }
    public bool SupportsFork { get; set; }
    public bool SupportsResume { get; set; }
    public bool SupportsClose { get; set; }
    public bool SupportsAdditionalDirectories { get; set; }
}

public sealed class AcpAgentCapabilities
{
    public bool LoadSession { get; set; }
    public AcpPromptCapabilities PromptCapabilities { get; set; } = new();
    public AcpSessionCapabilities SessionCapabilities { get; set; } = new();
}

public sealed class AcpAuthMethod
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Type { get; set; }
    public List<string>? Args { get; set; }
}

public sealed class AcpInitializeResult
{
    public int ProtocolVersion { get; set; } = AcpProtocolConstants.ProtocolVersion;
    public AcpAgentCapabilities AgentCapabilities { get; set; } = new();
    public List<AcpAuthMethod> AuthMethods { get; set; } = [];
    public AcpImplementation? AgentInfo { get; set; }
    public JsonElement? RawMeta { get; set; }
}

public sealed class AcpSessionMode
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}

public sealed class AcpSessionModeState
{
    public string CurrentModeId { get; set; } = "";
    public List<AcpSessionMode> AvailableModes { get; set; } = [];
}

public sealed class AcpConfigSelectOption
{
    public string Value { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? GroupName { get; set; }
}

public sealed class AcpSessionConfigOption
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary><c>mode</c>, <c>model</c>, <c>model_config</c>, <c>thought_level</c>, or custom.</summary>
    public string? Category { get; set; }
    /// <summary><c>select</c> or <c>boolean</c>.</summary>
    public string Type { get; set; } = "select";
    public string? CurrentValue { get; set; }
    public bool? CurrentBoolValue { get; set; }
    public List<AcpConfigSelectOption> Options { get; set; } = [];
}

public sealed class AcpModelInfo
{
    public string ModelId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public JsonElement? Meta { get; set; }
}

public sealed class AcpSessionModelState
{
    public string CurrentModelId { get; set; } = "";
    public List<AcpModelInfo> AvailableModels { get; set; } = [];
}

public sealed class AcpSessionStartResult
{
    public string Kind { get; set; } = "new";
    public string SessionId { get; set; } = "";
    public AcpSessionModeState? Modes { get; set; }
    public List<AcpSessionConfigOption> ConfigOptions { get; set; } = [];
    public AcpSessionModelState? Models { get; set; }
}

public sealed class AcpAvailableCommand
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? InputHint { get; set; }
    public bool IsSkill { get; set; }
    public JsonElement? Meta { get; set; }
}

public sealed class AcpUsage
{
    public long TotalTokens { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long? ThoughtTokens { get; set; }
    public long? CachedReadTokens { get; set; }
    public long? CachedWriteTokens { get; set; }
}

public sealed class AcpContextUsage
{
    public long Used { get; set; }
    public long Size { get; set; }
    public double? CostAmount { get; set; }
    public string? CostCurrency { get; set; }
}

public sealed class AcpPromptResult
{
    public string StopReason { get; set; } = "end_turn";
    public AcpUsage? Usage { get; set; }
    public JsonElement? Meta { get; set; }
}

public sealed class AcpToolDiff
{
    public string Path { get; set; } = "";
    public string? OldText { get; set; }
    public string NewText { get; set; } = "";
}

public sealed class AcpToolLocation
{
    public string Path { get; set; } = "";
    public int? Line { get; set; }
}

public sealed class AcpToolCallUpdate
{
    public string ToolCallId { get; set; } = "";
    /// <summary>Parent tool call id when this tool was executed inside a spawned subagent (e.g. Claude Code <c>parentToolUseId</c>).</summary>
    public string? ParentToolCallId { get; set; }
    public string? Title { get; set; }
    public string? Name { get; set; }
    /// <summary>Human-readable summary from <c>rawInput.description</c> (e.g. Bash description or SubAgent prompt summary).</summary>
    public string? Description { get; set; }
    /// <summary>read | edit | delete | move | search | execute | think | fetch | switch_mode | other</summary>
    public string? Kind { get; set; }
    /// <summary>pending | in_progress | completed | failed</summary>
    public string? Status { get; set; }
    public List<string> TextOutputs { get; set; } = [];
    public List<AcpToolDiff> Diffs { get; set; } = [];
    public List<AcpToolLocation> Locations { get; set; } = [];
    public string? Command { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public int? ExitCode { get; set; }
    /// <summary>True when started with <c>run_in_background: true</c> or reported as a background shell/subagent.</summary>
    public bool IsBackground { get; set; }
    /// <summary>True when this tool call spawns a subagent (<c>Agent</c> / <c>Task</c> / <c>invoke_subagent</c>).</summary>
    public bool IsSubagentLaunch { get; set; }
    /// <summary>True when the launch tool completes as soon as the subagent is spawned while the subagent keeps running asynchronously (e.g. Antigravity <c>invoke_subagent</c>).</summary>
    public bool SubagentRunsAsynchronously { get; set; }
    public string? SubagentType { get; set; }
    public string? SubagentModel { get; set; }
    public string? WorktreeIsolation { get; set; }
    /// <summary>Subagent conversation or tool-call IDs targeted by a management tool (e.g. Antigravity <c>manage_subagents</c>).</summary>
    public List<string> TargetSubagentIds { get; set; } = [];
    /// <summary>Identifier linking a background Bash/Task with follow-up <c>TaskOutput</c>/<c>BashOutput</c>/<c>TaskStop</c>/<c>KillShell</c> calls.</summary>
    public string? BackgroundTaskId { get; set; }
    /// <summary>Structured workflow tool kind: <c>enter_plan</c> | <c>exit_plan</c> | <c>todo_write</c> | <c>task_create</c> | <c>task_update</c> | <c>background_output</c> | <c>background_stop</c> | <c>subagent_stop</c> | <c>subagent_stop_all</c>.</summary>
    public string? WorkflowAction { get; set; }
    /// <summary>Tasks parsed from <c>TodoWrite</c>, <c>TaskCreate</c>, or <c>TaskUpdate</c>.</summary>
    public List<AcpPlanEntry> WorkflowTasks { get; set; } = [];
    /// <summary>Markdown plan payload extracted from <c>ExitPlanMode</c> or plan tools.</summary>
    public string? PlanMarkdown { get; set; }
    public JsonElement? RawInput { get; set; }
    public JsonElement? RawOutput { get; set; }
    public JsonElement? Meta { get; set; }
}

public sealed class AcpPlanEntry
{
    public string? Id { get; set; }
    public string Content { get; set; } = "";
    public string? ActiveForm { get; set; }
    public string Priority { get; set; } = "medium";
    /// <summary>pending | in_progress | completed | failed</summary>
    public string Status { get; set; } = "pending";
    public string? Owner { get; set; }
    public List<string> BlockedBy { get; set; } = [];
}

public sealed class AcpPermissionOption
{
    public string OptionId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>allow_once | allow_always | reject_once | reject_always</summary>
    public string Kind { get; set; } = "allow_once";
    public JsonElement? Meta { get; set; }
}

public sealed class AcpPermissionRequest
{
    public string RequestId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public AcpToolCallUpdate ToolCall { get; set; } = new();
    public List<AcpPermissionOption> Options { get; set; } = [];
    public string? WarningMessage { get; set; }
    public string? ActionDetail { get; set; }
    /// <summary>True when this request represents an interactive Plan approval gate (e.g. Claude Code <c>ExitPlanMode</c>).</summary>
    public bool IsPlanApproval { get; set; }
    /// <summary>Structured plan markdown to display inside the Plan Approval gate card.</summary>
    public string? PlanMarkdown { get; set; }
}

public sealed class AcpQuestionOption
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Description { get; set; }
}

public sealed class AcpQuestionItem
{
    public string Id { get; set; } = "";
    public string Prompt { get; set; } = "";
    public bool MultiSelect { get; set; }
    public bool AllowFreeText { get; set; }
    public List<AcpQuestionOption> Options { get; set; } = [];
}

/// <summary>Interactive user question (from <c>elicitation/create</c> or dialect question requests). Never auto-approved.</summary>
public sealed class AcpQuestionRequest
{
    public string RequestId { get; set; } = "";
    public string Title { get; set; } = "";
    public List<AcpQuestionItem> Questions { get; set; } = [];
}

/// <summary>A single item attached to a prompt sent by the user.</summary>
public sealed class AcpPromptAttachment
{
    /// <summary><c>file</c> or <c>image</c>.</summary>
    public string Kind { get; set; } = "file";
    public string Path { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? MimeType { get; set; }
    public string? Base64Data { get; set; }
}

/// <summary>Registry manifest entry from <c>cdn.agentclientprotocol.com/registry/v1/latest/registry.json</c>.</summary>
public sealed class AcpRegistryDocument
{
    public string? Version { get; set; }
    public List<AcpRegistryAgent> Agents { get; set; } = [];
}

public sealed class AcpRegistryAgent
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Description { get; set; }
    public AcpRegistryDistribution? Distribution { get; set; }
}

public sealed class AcpRegistryDistribution
{
    public AcpRegistryPackage? Npx { get; set; }
    public AcpRegistryPackage? Uvx { get; set; }
    public Dictionary<string, AcpRegistryBinary>? Binary { get; set; }
}

public sealed class AcpRegistryPackage
{
    public string Package { get; set; } = "";
    public List<string>? Args { get; set; }
}

public sealed class AcpRegistryBinary
{
    public string Archive { get; set; } = "";
    public string Cmd { get; set; } = "";
    public List<string>? Args { get; set; }
    public string? Sha256 { get; set; }
}
