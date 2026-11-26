using System.Text.Json;

namespace Octopus.Acp.Protocol;

/// <summary>ACP protocol constants (schema-v1.21.0 + v2 preview negotiation).</summary>
public static class AcpProtocolConstants
{
    public const int ProtocolVersion = 1;
    public const int MinSupportedVersion = 1;
    public const int MaxSupportedVersion = 2;
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

public sealed class AcpMcpCapabilities
{
    public bool Http { get; set; }
    public bool Sse { get; set; }
}

public sealed class AcpAuthCapabilities
{
    public bool Logout { get; set; }
    public bool Terminal { get; set; }
}

public sealed class AcpSessionCapabilities
{
    public bool SupportsList { get; set; }
    public bool SupportsDelete { get; set; }
    public bool SupportsFork { get; set; }
    public bool SupportsResume { get; set; }
    public bool SupportsClose { get; set; }
    public bool SupportsAdditionalDirectories { get; set; }
    public bool SupportsArchive { get; set; }
    public bool SupportsCompaction { get; set; }
    public bool SupportsCursors { get; set; }
}

public sealed class AcpAgentCapabilities
{
    public bool LoadSession { get; set; }
    public AcpPromptCapabilities PromptCapabilities { get; set; } = new();
    public AcpMcpCapabilities McpCapabilities { get; set; } = new();
    public AcpAuthCapabilities AuthCapabilities { get; set; } = new();
    public AcpSessionCapabilities SessionCapabilities { get; set; } = new();
}

public sealed class AcpAuthEnvVar
{
    public string Name { get; set; } = "";
    public string? Label { get; set; }
    public bool Secret { get; set; } = true;
    public string? Link { get; set; }
}

public sealed class AcpAuthMethod
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary><c>agent</c> (default/null), <c>terminal</c>, or <c>env_var</c>.</summary>
    public string? Type { get; set; }
    public List<string>? Args { get; set; }
    public List<AcpAuthEnvVar>? EnvVars { get; set; }
    public string? Link { get; set; }
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

public sealed class AcpSessionInfo
{
    public string SessionId { get; set; } = "";
    public string? Cwd { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public JsonElement? Meta { get; set; }
}

public sealed class AcpSessionListResult
{
    public List<AcpSessionInfo> Sessions { get; set; } = [];
    public string? NextCursor { get; set; }
}

public sealed class AcpSessionInfoUpdate
{
    public string? Title { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public JsonElement? Meta { get; set; }
}

public sealed class AcpCompactionUpdate
{
    public string CompactionId { get; set; } = "";
    /// <summary><c>started</c>, <c>in_progress</c>, <c>completed</c>, or <c>failed</c>.</summary>
    public string Status { get; set; } = "in_progress";
    public string? Summary { get; set; }
    public long? PreTokens { get; set; }
    public long? PostTokens { get; set; }
    public JsonElement? Meta { get; set; }
}

public sealed class AcpCursorUpdate
{
    public string CursorId { get; set; } = "";
    public string? MessageId { get; set; }
    public JsonElement? Meta { get; set; }
}

public sealed class AcpContentAnnotations
{
    public List<string> Audience { get; set; } = [];
    public double? Priority { get; set; }
    public DateTimeOffset? LastModified { get; set; }
}

/// <summary>An audio content block (<c>type: "audio"</c>) from ACP messages or prompts.</summary>
public sealed class AcpAudioContent
{
    public string Base64Data { get; set; } = "";
    public string MimeType { get; set; } = "audio/wav";
    public AcpContentAnnotations? Annotations { get; set; }
}

/// <summary>An embedded resource content block (<c>type: "resource"</c>).</summary>
public sealed class AcpEmbeddedResourceContent
{
    public string Uri { get; set; } = "";
    public string? MimeType { get; set; }
    public string? Text { get; set; }
    public string? Blob { get; set; }
    public AcpContentAnnotations? Annotations { get; set; }
}

/// <summary>An image returned by an ACP agent message or tool call (standard ACP <c>type: "image"</c> content block or saved file).</summary>
public sealed class AcpImageContent
{
    public string? Base64Data { get; set; }
    public string? MimeType { get; set; }
    public string? Uri { get; set; }
    public string? FilePath { get; set; }
    public string? AltText { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public AcpContentAnnotations? Annotations { get; set; }
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
    /// <summary>read | edit | delete | move | search | execute | think | fetch | switch_mode | image | other</summary>
    public string? Kind { get; set; }
    /// <summary>pending | in_progress | completed | failed</summary>
    public string? Status { get; set; }
    public List<string> TextOutputs { get; set; } = [];
    public List<AcpImageContent> Images { get; set; } = [];
    public List<AcpToolDiff> Diffs { get; set; } = [];
    public List<AcpToolLocation> Locations { get; set; } = [];
    /// <summary>Client-hosted ACP terminal IDs referenced by <c>type: "terminal"</c> tool call content blocks.</summary>
    public List<string> TerminalIds { get; set; } = [];
    /// <summary>True when this tool call generates or edits an image (e.g. Codex <c>image_generation</c>, Cursor <c>cursor/generate_image</c>, MCP image tools).</summary>
    public bool IsImageGeneration { get; set; }
    /// <summary>Prompt or revised prompt associated with an image generation call.</summary>
    public string? ImagePrompt { get; set; }
    /// <summary>Normalized generation progress in <c>[0, 1]</c> when reported by the agent or tool.</summary>
    public double? ImageProgress { get; set; }
    /// <summary>Requested or detected width/height aspect ratio for the generated image canvas.</summary>
    public double? ImageAspectRatio { get; set; }
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
    /// <summary>Full task prompt dispatched to a spawned subagent (e.g. <c>Agent.prompt</c> or <c>invoke_subagent.Prompt</c>).</summary>
    public string? SubagentPrompt { get; set; }
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
    /// <summary>JSON Schema primitive type: <c>string</c>, <c>boolean</c>, <c>number</c>, <c>integer</c>, or <c>array</c>.</summary>
    public string ValueType { get; set; } = "string";
    public bool Required { get; set; }
    public string? DefaultValue { get; set; }
    public double? Minimum { get; set; }
    public double? Maximum { get; set; }
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public string? Format { get; set; }
    public bool MultiSelect { get; set; }
    public bool AllowFreeText { get; set; }
    public List<AcpQuestionOption> Options { get; set; } = [];
}

/// <summary>Decision returned for an ACP <c>elicitation/create</c> request.</summary>
public enum AcpElicitationAction
{
    Accept,
    Decline,
    Cancel,
}

/// <summary>Result of a form elicitation prompt (supports explicit accept, decline, or cancel).</summary>
public sealed record AcpFormElicitationResponse(
    AcpElicitationAction Action,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Answers = null);

/// <summary>Interactive user question (from <c>elicitation/create</c> mode=form or dialect question requests). Never auto-approved.</summary>
public sealed class AcpQuestionRequest
{
    public string RequestId { get; set; } = "";
    public string? ElicitationId { get; set; }
    public string Title { get; set; } = "";
    public List<AcpQuestionItem> Questions { get; set; } = [];
}

/// <summary>Out-of-band URL elicitation request (from <c>elicitation/create</c> mode=url, e.g. OAuth/browser authorization).</summary>
public sealed class AcpUrlElicitationRequest
{
    public string RequestId { get; set; } = "";
    public string ElicitationId { get; set; } = "";
    public string? SessionId { get; set; }
    public string Message { get; set; } = "";
    public string Url { get; set; } = "";
}

/// <summary>A single item attached to a prompt sent by the user.</summary>
public sealed class AcpPromptAttachment
{
    /// <summary><c>file</c>, <c>image</c>, <c>audio</c>, or <c>resource</c>.</summary>
    public string Kind { get; set; } = "file";
    public string Path { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? MimeType { get; set; }
    public string? Base64Data { get; set; }
    /// <summary>Optional inline text payload used when emitting ACP <c>type: "resource"</c> embedded context blocks.</summary>
    public string? TextContent { get; set; }
    public AcpContentAnnotations? Annotations { get; set; }
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
