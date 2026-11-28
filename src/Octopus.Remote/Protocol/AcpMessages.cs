using System.Text.Json.Serialization;

namespace Octopus.Remote.Protocol;

// A host's agent that runs as an ACP conversation in a GUI tab, shown on a device (design/ui/移动端功能扩展方案.html, G-A).
// The host keeps the conversation (the agent's process, its permissions, its dialect); the device sees a flattened
// copy of the tab's timeline and sends prompts, answers and choices back. The computer and the device look at the
// same conversation: whatever is answered on one closes the card on the other.
//
// acp/attach    device → host  AcpRefMessage           → AcpSnapshotMessage (the latest entries and the sequence number)
// acp/detach    device → host  AcpRefMessage           → Nothing
// acp/prompt    device → host  AcpPromptMessage        → Nothing (queued by the tab when a turn runs)
// acp/cancel    device → host  AcpCancelMessage        → Nothing (unscoped Stop when collab fields are omitted)
// acp/respond   device → host  AcpRespondMessage       → Nothing (a permission or question card)
// acp/setOption device → host  AcpSetOptionMessage     → Nothing
// acp/mentionTargets device → host  AcpMentionTargetsRequestMessage → AcpMentionTargetsMessage (sessions and agents
//               the prompt may @-mention; only with the acp.mentions capability)
// acp/update    host → device  AcpUpdateMessage (event, for each attached item; Sequence grows by one per event)
//
// A device that sees a gap in Sequence (it was away) attaches again for a fresh snapshot.

/// <summary>Names one GUI conversation: the item's id.</summary>
public sealed class AcpRefMessage
{
    public string ItemId { get; set; } = "";
}

/// <summary>
/// Stops a GUI conversation's active turn. When collaboration identity fields are omitted (or null), acts as an
/// unconditional Stop (wire-compatible with <see cref="AcpRefMessage"/>); when set on a host with
/// <see cref="RemoteMethods.CapabilityAcpCollabCancel"/>, cancels only if the host's active turn still matches.
/// </summary>
public sealed class AcpCancelMessage
{
    public string ItemId { get; set; } = "";
    public string? CollabThreadId { get; set; }
    public string? CollabTaskId { get; set; }
    public string? CollabDeliveryId { get; set; }
}

/// <summary>How a GUI conversation is doing, as a device shows it.</summary>
public static class AcpStates
{
    /// <summary>The agent's process is starting or the conversation is being loaded.</summary>
    public const string Connecting = "connecting";
    /// <summary>Waiting for the next prompt.</summary>
    public const string Ready = "ready";
    /// <summary>A turn runs.</summary>
    public const string Working = "working";
    /// <summary>A permission or question card waits for an answer.</summary>
    public const string Waiting = "waiting";
    /// <summary>The agent could not be started or the connection to it broke; <see cref="AcpSnapshotMessage.ErrorText"/> says why.</summary>
    public const string Error = "error";
}

/// <summary>What an entry of the timeline is.</summary>
public static class AcpEntryKinds
{
    /// <summary>What the person sent: <see cref="AcpEntryMessage.Text"/> and <see cref="AcpEntryMessage.Attachments"/>.</summary>
    public const string User = "user";
    /// <summary>One turn of the agent: thought, Markdown, tools and plan.</summary>
    public const string Assistant = "assistant";
    /// <summary>The agent asks to be allowed to do something: <see cref="AcpEntryMessage.Permission"/>.</summary>
    public const string Permission = "permission";
    /// <summary>The agent asks the person questions: <see cref="AcpEntryMessage.Question"/>.</summary>
    public const string Question = "question";
}

/// <summary>A conversation as it is when a device attaches.</summary>
public sealed class AcpSnapshotMessage
{
    public string ItemId { get; set; } = "";
    /// <summary>The number of the last update included; the next <see cref="AcpUpdateMessage"/> has this plus one.</summary>
    public long Sequence { get; set; }
    /// <summary>One of <see cref="AcpStates"/>.</summary>
    public string State { get; set; } = AcpStates.Connecting;
    /// <summary>A line about what it is doing (starting the agent, installing an adapter…); null when nothing to say.</summary>
    public string? StatusDetail { get; set; }
    /// <summary>Why it failed, when <see cref="State"/> is <see cref="AcpStates.Error"/>.</summary>
    public string? ErrorText { get; set; }
    /// <summary>The agent's name, for the header.</summary>
    public string AgentName { get; set; } = "";
    /// <summary>The latest entries in order, oldest first (at most <see cref="AcpLimits.SnapshotEntries"/>).</summary>
    public List<AcpEntryMessage> Entries { get; set; } = [];
    /// <summary>True when older entries were left out.</summary>
    public bool HasEarlier { get; set; }
    /// <summary>Prompts queued while a turn runs.</summary>
    public int QueuedCount { get; set; }
    public AcpComposerMessage Composer { get; set; } = new();
    /// <summary>Session-level workflow task list (<c>TodoWrite</c>, <c>TaskCreate</c>/<c>TaskUpdate</c>, <c>update_steps</c>, or ACP <c>plan</c>).</summary>
    public List<AcpWorkflowTaskMessage>? Tasks { get; set; }
}

/// <summary>What changed since the last update. Entries are sent whole whenever anything in them changed.</summary>
public sealed class AcpUpdateMessage
{
    public string ItemId { get; set; } = "";
    public long Sequence { get; set; }
    /// <summary>New or changed entries; a new one goes after <see cref="AcpEntryMessage.AfterId"/> (null: at the end).</summary>
    public List<AcpEntryMessage> Upserts { get; set; } = [];
    /// <summary>Ids of entries that left the timeline.</summary>
    public List<string> Removed { get; set; } = [];
    /// <summary>Set when it changed; null when it did not.</summary>
    public string? State { get; set; }
    public string? StatusDetail { get; set; }
    public string? ErrorText { get; set; }
    public int? QueuedCount { get; set; }
    /// <summary>Set when the choices or the chosen values changed.</summary>
    public AcpComposerMessage? Composer { get; set; }
    /// <summary>Set when the session-level task list changed (empty list means cleared; null means unchanged).</summary>
    public List<AcpWorkflowTaskMessage>? Tasks { get; set; }
}

/// <summary>One entry of the timeline. Only the fields of its <see cref="Kind"/> are set.</summary>
public sealed class AcpEntryMessage
{
    /// <summary>Stable for the life of the entry on the host.</summary>
    public string Id { get; set; } = "";
    /// <summary>One of <see cref="AcpEntryKinds"/>.</summary>
    public string Kind { get; set; } = AcpEntryKinds.Assistant;
    /// <summary>In an update: the entry a new one goes after; null puts it at the end. Ignored for an entry the device has.</summary>
    public string? AfterId { get; set; }
    /// <summary>User: what was sent. Assistant: the answer as Markdown (cut to <see cref="AcpLimits.TextChars"/> from the start).</summary>
    public string? Text { get; set; }
    /// <summary>Assistant: what it thought (cut like <see cref="Text"/>).</summary>
    public string? Thought { get; set; }
    /// <summary>Assistant: still streaming.</summary>
    public bool IsStreaming { get; set; }
    /// <summary>Assistant: why the turn failed.</summary>
    public string? ErrorText { get; set; }
    /// <summary>Assistant: why the turn stopped when it did not end normally (for example, <c>cancelled</c>). Null on normal completion or older hosts.</summary>
    public string? StopReason { get; set; }
    /// <summary>Assistant: how long the turn took, as the host words it ("12s").</summary>
    public string? Duration { get; set; }
    /// <summary>User: the names of the attached files and images.</summary>
    public List<string>? Attachments { get; set; }
    /// <summary>Assistant: its tool calls in order.</summary>
    public List<AcpToolMessage>? Tools { get; set; }
    /// <summary>Assistant: intermediate text segments folded into the work area.</summary>
    public List<AcpWorkNoteMessage>? Notes { get; set; }
    /// <summary>Assistant: its plan, when it made one.</summary>
    public List<AcpPlanEntryMessage>? Plan { get; set; }
    public AcpPermissionMessage? Permission { get; set; }
    public AcpQuestionMessage? Question { get; set; }
    /// <summary>Optional structured collaboration attribution preserved across display text or snapshot truncation; null for ordinary turns or older hosts.</summary>
    public AcpCollabCorrelationMessage? Collab { get; set; }
}

/// <summary>Structured collaboration attribution carried alongside a timeline entry independently of display text.</summary>
public sealed class AcpCollabCorrelationMessage
{
    public string? ThreadId { get; set; }
    public string? TaskId { get; set; }
    public string? DeliveryId { get; set; }
    public bool ExpectsReply { get; set; }
    /// <summary>True when this turn ended normally while unfinished host-side child delegations owned by the turn remain active; omitted on older hosts or ordinary completions.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Deferred { get; set; }
}

/// <summary>One intermediate assistant text segment folded into the work area.</summary>
public sealed class AcpWorkNoteMessage
{
    public string Text { get; set; } = "";
    /// <summary>Zero-based index in <see cref="AcpEntryMessage.Tools"/> before which this text appeared.</summary>
    public int BeforeToolIndex { get; set; }
}

/// <summary>One tool call of a turn.</summary>
public sealed class AcpToolMessage
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>ACP's kind: <c>read</c>, <c>edit</c>, <c>delete</c>, <c>move</c>, <c>search</c>, <c>execute</c>, <c>think</c>, <c>fetch</c>, <c>other</c>.</summary>
    public string Kind { get; set; } = "other";
    /// <summary><c>pending</c>, <c>in_progress</c>, <c>completed</c> or <c>failed</c>.</summary>
    public string Status { get; set; } = "in_progress";
    /// <summary>The command line of an <c>execute</c> call.</summary>
    public string? Command { get; set; }
    /// <summary>The file it is mostly about.</summary>
    public string? Path { get; set; }
    public int Added { get; set; }
    public int Removed { get; set; }
    public int? ExitCode { get; set; }
    /// <summary>What it printed or returned, its last <see cref="AcpLimits.ToolOutputChars"/> characters.</summary>
    public string? Output { get; set; }
    /// <summary>The diff of an edit, one line each with its <c>+</c>, <c>-</c> or space first (at most <see cref="AcpLimits.DiffLines"/>).</summary>
    public List<string>? Diff { get; set; }
}

public sealed class AcpPlanEntryMessage
{
    public string Content { get; set; } = "";
    /// <summary><c>pending</c>, <c>in_progress</c> or <c>completed</c>.</summary>
    public string Status { get; set; } = "pending";
}

/// <summary>One task in the session-level workflow task list.</summary>
public sealed class AcpWorkflowTaskMessage
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary><c>pending</c>, <c>in_progress</c>, <c>completed</c> or <c>failed</c>.</summary>
    public string Status { get; set; } = "pending";
    /// <summary>Optional live gerund/progress text shown while <c>in_progress</c>.</summary>
    public string? ActiveForm { get; set; }
    /// <summary>Optional subagent owner name.</summary>
    public string? Owner { get; set; }
    /// <summary>Optional prerequisite task ids blocking this task.</summary>
    public List<string>? BlockedBy { get; set; }
}

/// <summary>A permission card.</summary>
public sealed class AcpPermissionMessage
{
    public string Title { get; set; } = "";
    /// <summary>What exactly it wants to do (a command, a path).</summary>
    public string? Detail { get; set; }
    /// <summary>A warning the dialect attached (a dangerous command, a question it must not answer itself).</summary>
    public string? Warning { get; set; }
    public List<AcpOptionMessage> Options { get; set; } = [];
    public bool IsResolved { get; set; }
    /// <summary>What was chosen, once resolved ("Allowed once").</summary>
    public string? ResolvedSummary { get; set; }
}

/// <summary>A choice: a permission option, a model, a mode, an answer to a question.</summary>
public sealed class AcpOptionMessage
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Permission options: ACP's kind (<c>allow_once</c>, <c>allow_always</c>, <c>reject_once</c>, <c>reject_always</c>). Answers: a description.</summary>
    public string? Detail { get; set; }
}

/// <summary>A question card: one or more questions, each with choices and maybe free text.</summary>
public sealed class AcpQuestionMessage
{
    public string Title { get; set; } = "";
    public List<AcpQuestionFieldMessage> Fields { get; set; } = [];
    public bool IsResolved { get; set; }
    public string? ResolvedSummary { get; set; }
}

public sealed class AcpQuestionFieldMessage
{
    public string Id { get; set; } = "";
    public string Prompt { get; set; } = "";
    public bool MultiSelect { get; set; }
    public bool AllowFreeText { get; set; }
    public List<AcpOptionMessage> Choices { get; set; } = [];
}

/// <summary>What can be chosen above the input: model, mode (permissions) and effort, each with the current value.</summary>
public sealed class AcpComposerMessage
{
    public List<AcpOptionMessage> Models { get; set; } = [];
    public string? Model { get; set; }
    public List<AcpOptionMessage> Modes { get; set; } = [];
    public string? Mode { get; set; }
    public List<AcpOptionMessage> Efforts { get; set; } = [];
    public string? Effort { get; set; }
}

public sealed class AcpPromptMessage
{
    public string ItemId { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Paths inside the item's project (or uploaded for the session via <c>fs/upload</c>) to attach.</summary>
    public List<string>? Attachments { get; set; }
    /// <summary>
    /// Sessions and agents @-mentioned inline in <see cref="Text"/>, in order of appearance; each one's
    /// <see cref="AcpMentionMessage.Text"/> appears verbatim in the text. Sent only to hosts with the acp.mentions capability.
    /// </summary>
    public List<AcpMentionMessage>? Mentions { get; set; }
    /// <summary>When true and a turn is active, interrupts the active turn gracefully and sends this prompt immediately instead of queueing it.</summary>
    public bool Immediate { get; set; }
    /// <summary>Optional per-delivery collaboration identifier echoed on the resulting timeline turn entries; ignored by older hosts.</summary>
    public string? CollabDeliveryId { get; set; }
}

/// <summary>One inline mention of a prompt: the token as written (<c>@Codex（重构登录）</c>) and what it points at.</summary>
public sealed class AcpMentionMessage
{
    /// <summary><c>session</c> (an existing session of the host) or <c>agent</c> (an agent the conversation may start).</summary>
    public string Kind { get; set; } = "";
    /// <summary>The session's item id, or the agent's id.</summary>
    public string TargetId { get; set; } = "";
    public string Text { get; set; } = "";
    public string? AgentId { get; set; }
}

public sealed class AcpMentionTargetsRequestMessage
{
    /// <summary>The conversation being written in (it is left out of the sessions).</summary>
    public string ItemId { get; set; } = "";
    /// <summary>What follows the <c>@</c>; empty for everything.</summary>
    public string Query { get; set; } = "";
}

public sealed class AcpMentionTargetsMessage
{
    public List<AcpMentionTargetMessage> Targets { get; set; } = [];
}

/// <summary>Something a prompt can @-mention, with what the device needs to write its token and show it in a list.</summary>
public sealed class AcpMentionTargetMessage
{
    /// <summary><c>session</c> or <c>agent</c>.</summary>
    public string Kind { get; set; } = "";
    public string TargetId { get; set; } = "";
    /// <summary>The agent's name (<c>Codex</c>): the token's main part.</summary>
    public string Name { get; set; } = "";
    /// <summary>A session's title, which tells sessions of the same agent apart: <c>@Codex（重构登录）</c>.</summary>
    public string? Detail { get; set; }
    public string? AgentId { get; set; }
    /// <summary>A second line for the list: a session's branch and status.</summary>
    public string? Subtitle { get; set; }
}

/// <summary>An answer to a card: <see cref="OptionId"/> for a permission; <see cref="Answers"/> for a question.</summary>
public sealed class AcpRespondMessage
{
    public string ItemId { get; set; } = "";
    /// <summary>The card's entry id.</summary>
    public string EntryId { get; set; } = "";
    public string? OptionId { get; set; }
    /// <summary>Question: the chosen choice ids by field id.</summary>
    public Dictionary<string, List<string>>? Answers { get; set; }
    /// <summary>Question: free text by field id.</summary>
    public Dictionary<string, string>? FreeText { get; set; }
}

public static class AcpOptionKinds
{
    public const string Model = "model";
    public const string Mode = "mode";
    public const string Effort = "effort";
}

public sealed class AcpSetOptionMessage
{
    public string ItemId { get; set; } = "";
    /// <summary>One of <see cref="AcpOptionKinds"/>.</summary>
    public string Kind { get; set; } = AcpOptionKinds.Model;
    public string Value { get; set; } = "";
}

/// <summary>How much of a conversation goes over the wire.</summary>
public static class AcpLimits
{
    public const int SnapshotEntries = 40;
    public const int TextChars = 32 * 1024;
    public const int ToolOutputChars = 4 * 1024;
    public const int DiffLines = 200;
}
