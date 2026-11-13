namespace Octopus.Agents.Sessions;

/// <summary>
/// One saved conversation of an agent in a project folder, as its own files on disk describe it (design/ui/会话管理方案.html).
/// </summary>
/// <param name="AgentId">The agent definition's id (<c>claude</c>, <c>codex</c>…).</param>
/// <param name="AccountDirectory">The account's configuration folder the session lives in (a <c>CLAUDE_CONFIG_DIR</c>, a <c>CODEX_HOME</c>).</param>
/// <param name="SessionId">The id the agent resumes it by.</param>
/// <param name="Title">What the session is about: the agent's own title when it wrote one, else the first prompt; never empty.</param>
/// <param name="StartedAt">The first record's time.</param>
/// <param name="UpdatedAt">The last record's time (the file's write time when records carry none).</param>
/// <param name="MessageCount">User and assistant messages, not counting tool results and bookkeeping records.</param>
/// <param name="Model">The model of the last assistant message, as the agent names it; null when unknown.</param>
/// <param name="GitBranch">The branch recorded with the session; null when none.</param>
/// <param name="FilePath">The transcript file.</param>
public sealed record AgentSessionInfo(
    string AgentId,
    string AccountDirectory,
    string SessionId,
    string Title,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    string? Model,
    string? GitBranch,
    string FilePath);

public enum SessionTurnRole
{
    User,
    Assistant,
    /// <summary>A tool the assistant called, with what it was given (<see cref="SessionTurn.Text"/>) and what came back (<see cref="SessionTurn.Output"/>).</summary>
    Tool,
}

/// <summary>One step of a conversation, flattened to text: enough to hand the work over to another agent.</summary>
/// <param name="Text">User and assistant: the message text. Tool: a one-line summary of the call's input (a path, a command).</param>
/// <param name="ToolName">Tool only: the tool's name as the agent calls it (<c>Read</c>, <c>Bash</c>, <c>shell</c>…).</param>
/// <param name="Output">Tool only: the result, cut to a few kilobytes; null when there was none.</param>
/// <param name="Files">Tool only: files the call wrote or edited (absolute or as given), for the handoff's list of changed files.</param>
public sealed record SessionTurn(
    SessionTurnRole Role,
    string Text,
    DateTimeOffset? Timestamp = null,
    string? ToolName = null,
    string? Output = null,
    IReadOnlyList<string>? Files = null);

/// <summary>A session's conversation, oldest first. Sidechains (subagents), meta records and system reminders are left out.</summary>
public sealed record SessionTranscript(AgentSessionInfo Session, IReadOnlyList<SessionTurn> Turns);

/// <summary>Reads one agent's saved sessions. One implementation per agent whose files Octopus understands.</summary>
public interface ISessionSource
{
    /// <summary>The agent definition id this source reads.</summary>
    string AgentId { get; }

    /// <summary>
    /// The sessions of <paramref name="projectPath"/> (the working directory they were started in) kept under one
    /// account folder, newest first. Files that cannot be read are skipped; a missing folder gives an empty list.
    /// </summary>
    Task<IReadOnlyList<AgentSessionInfo>> ListAsync(string accountDirectory, string projectPath, CancellationToken cancellationToken = default);

    /// <summary>The whole conversation of a session listed by <see cref="ListAsync"/>.</summary>
    Task<SessionTranscript> ReadAsync(AgentSessionInfo session, CancellationToken cancellationToken = default);
}

/// <summary>How a session reaches another agent.</summary>
public enum SessionMigrationKind
{
    /// <summary>Written as the target's own session file and resumed there: history and tool calls stay.</summary>
    Native,
    /// <summary>Summarised into a handoff note that starts a new session of the target; works with any agent that takes a prompt.</summary>
    Handoff,
}

/// <summary>What a migration should produce.</summary>
/// <param name="TargetAgentId">The agent the session moves to (may be the source agent, for another account).</param>
/// <param name="TargetAccountDirectory">The target account's folder; null for the agent's default.</param>
/// <param name="ProjectPath">The project folder the new session works in.</param>
/// <param name="IncludeToolOutput">Native: keep tool results. Handoff: quote short tool results next to the calls.</param>
/// <param name="RecentTurns">Handoff: only the last this-many user turns (with what followed them); null for all.</param>
/// <param name="HandoffDirectory">Handoff: where the full transcript note is written.</param>
/// <param name="Language">Handoff: the language of the note's headings and instructions (a BCP 47 tag such as <c>zh-CN</c>); English otherwise.</param>
public sealed record SessionMigrationRequest(
    string TargetAgentId,
    string? TargetAccountDirectory,
    string ProjectPath,
    bool IncludeToolOutput = true,
    int? RecentTurns = null,
    string? HandoffDirectory = null,
    string? Language = null);

/// <summary>The outcome: either a session to resume, or a prompt to start a new one with.</summary>
/// <param name="ResumeSessionId">Native: the id to resume in the target.</param>
/// <param name="Prompt">Handoff: the first message of the new session.</param>
/// <param name="NotePath">Handoff: the file with the full note, which the prompt points at; null when everything fit in the prompt.</param>
public sealed record SessionMigrationResult(
    SessionMigrationKind Kind,
    string? ResumeSessionId = null,
    string? Prompt = null,
    string? NotePath = null);

/// <summary>Moves a session from one agent (or account) to another. Converters are registered per source and target.</summary>
public interface ISessionConverter
{
    SessionMigrationKind Kind { get; }

    /// <summary>Whether this converter can take a session of <paramref name="sourceAgentId"/> to <paramref name="targetAgentId"/>.</summary>
    bool CanConvert(string sourceAgentId, string targetAgentId);

    Task<SessionMigrationResult> ConvertAsync(SessionTranscript transcript, SessionMigrationRequest request, CancellationToken cancellationToken = default);
}
