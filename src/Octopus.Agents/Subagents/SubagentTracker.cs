using System.Text.Json;

namespace Octopus.Agents.Subagents;

/// <summary>Where a subagent is in its life.</summary>
public enum SubagentState
{
    Running,
    Done,
    Failed,
    /// <summary>It never reported its end: the turn or the session ended first (the user pressed Esc, the agent exited).</summary>
    Interrupted,
}

/// <summary>One subagent a session started, as far as its signals and transcript tell.</summary>
public sealed class SubagentRun
{
    internal SubagentRun(string id, string type, DateTimeOffset startedAt)
    {
        Id = id;
        Type = type;
        StartedAt = startedAt;
    }

    /// <summary>The agent's own id (<c>agent_id</c>), letters and digits only.</summary>
    public string Id { get; }

    /// <summary>The subagent type, e.g. <c>Explore</c> or <c>general-purpose</c>.</summary>
    public string Type { get; internal set; }

    /// <summary>The short task the parent gave it (the Agent tool's <c>description</c>); null until known.</summary>
    public string? Description { get; internal set; }

    /// <summary>Started with <c>run_in_background</c>: it may outlive the turn that started it.</summary>
    public bool Background { get; internal set; }

    public SubagentState State { get; internal set; } = SubagentState.Running;
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? EndedAt { get; internal set; }

    /// <summary>Tool calls read from its transcript so far.</summary>
    public int ToolCount { get; internal set; }

    /// <summary>Its latest tool call, e.g. <c>Read SignalHub.cs</c>; null before the first.</summary>
    public string? LastAction { get; internal set; }

    /// <summary>The Agent tool call that started it, once known; ties a failure back to it.</summary>
    public string? ToolUseId { get; internal set; }

    /// <summary>Its own transcript (<c>…/&lt;session&gt;/subagents/agent-&lt;id&gt;.jsonl</c>); null when the session's is unknown.</summary>
    public string? TranscriptPath { get; internal set; }

    /// <summary>How far its transcript has been read.</summary>
    public long TranscriptOffset { get; internal set; }

    /// <summary>The <c>.meta.json</c> next to the transcript was read (it names the description and the tool call).</summary>
    public bool MetaRead { get; internal set; }

    public bool IsFinished => State != SubagentState.Running;

    /// <summary>Its transcript was read after it ended, so nothing more will come from it.</summary>
    public bool Settled { get; internal set; }

    /// <summary>Whether a read of its transcript can still find something.</summary>
    public bool NeedsRead => !Settled;
}

/// <summary>
/// The subagents of one session, built from Claude Code's hook signals: <c>subagent-launch</c> (PreToolUse of the
/// Agent tool: the description), <c>subagent-start</c> / <c>subagent-stop</c> (SubagentStart/Stop: the agent id),
/// <c>subagent-failed</c> (PostToolUseFailure of the Agent tool), and the session's own turn signals. Each hook runs
/// its own forwarder process, so signals of one moment may arrive in any order; every step tolerates that.
/// Not thread-safe: the caller serializes.
/// </summary>
public sealed class SubagentTracker
{
    private readonly List<SubagentRun> _runs = [];
    private readonly List<Launch> _launches = [];

    public IReadOnlyList<SubagentRun> Runs => _runs;

    public bool HasRunning => _runs.Any(r => r.State == SubagentState.Running);

    /// <summary>Applies one signal. Returns whether the list changed.</summary>
    public bool Apply(string channel, string payload, DateTimeOffset now)
    {
        switch (channel)
        {
            case "subagent-launch": return OnLaunch(payload);
            case "subagent-start": return OnStart(payload, now);
            case "subagent-stop": return OnStop(payload, now);
            case "subagent-failed": return OnFailed(payload, now);
            case "prompt-submit": return OnNewTurn();
            case "stop":
            case "stop-failure":
                // A foreground subagent is a tool call of the turn: the turn cannot end while it runs.
                return Interrupt(r => !r.Background, now);
            case "session-start":
                // A compaction restarts the session's context, not its work.
                return Read(payload, "source") == "compact" ? false : Clear();
            case "session-end": return Interrupt(_ => true, now);
            default: return false;
        }
    }

    /// <summary>The session's process ended: whatever still runs, stopped with it.</summary>
    public bool EndSession(DateTimeOffset now) => Interrupt(_ => true, now);

    /// <summary>
    /// Takes what a read of the transcript found (see <see cref="SubagentTranscript"/>). <paramref name="final"/>: the
    /// read started after the run ended, so it saw everything; the run is not read again. Returns whether anything changed.
    /// </summary>
    public bool ApplyProgress(SubagentRun run, SubagentProgress progress, bool final = false)
    {
        if (!_runs.Contains(run) || run.TranscriptOffset != progress.FromOffset)
        {
            // Read twice, or the run is gone: the older read is stale.
            return false;
        }
        if (final && progress.ToOffset - progress.FromOffset < SubagentTranscript.MaxReadBytes)
        {
            run.Settled = true;
        }
        var changed = false;
        run.TranscriptOffset = progress.ToOffset;
        if (progress.Tools > 0)
        {
            run.ToolCount += progress.Tools;
            changed = true;
        }
        if (progress.LastAction is { } action && action != run.LastAction)
        {
            run.LastAction = action;
            changed = true;
        }
        if (progress.Meta is { } meta && !run.MetaRead)
        {
            run.MetaRead = true;
            if (meta.Description is { Length: > 0 } description && description != run.Description)
            {
                run.Description = description;
                changed = true;
            }
            run.ToolUseId ??= meta.ToolUseId;
            if (meta.Background && !run.Background)
            {
                run.Background = true;
            }
        }
        return changed;
    }

    private bool OnLaunch(string payload)
    {
        using var doc = Parse(payload);
        if (doc is null)
        {
            return false;
        }
        var root = doc.RootElement;
        var input = root.TryGetProperty("tool_input", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
        if (input.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        var launch = new Launch(
            String(root, "tool_use_id"),
            String(input, "description"),
            String(input, "subagent_type") ?? "general-purpose",
            input.TryGetProperty("run_in_background", out var bg) && bg.ValueKind == JsonValueKind.True);
        // Its SubagentStart may have come first: give the description to the oldest run of the type still without one.
        if (_runs.FirstOrDefault(r => r.Type == launch.Type && r.Description is null && r.ToolUseId is null) is { } run)
        {
            Bind(run, launch);
            return true;
        }
        _launches.Add(launch);
        return false;
    }

    private bool OnStart(string payload, DateTimeOffset now)
    {
        using var doc = Parse(payload);
        if (doc is null || AgentIdOf(doc.RootElement) is not { } id)
        {
            return false;
        }
        var root = doc.RootElement;
        if (_runs.Any(r => r.Id == id))
        {
            return false;
        }
        var type = String(root, "agent_type") ?? "general-purpose";
        var run = new SubagentRun(id, type, now) { TranscriptPath = SubagentTranscript.PathFor(String(root, "transcript_path"), id) };
        if (_launches.FirstOrDefault(l => l.Type == type) is { } launch)
        {
            _launches.Remove(launch);
            Bind(run, launch);
        }
        _runs.Add(run);
        return true;
    }

    private bool OnStop(string payload, DateTimeOffset now)
    {
        using var doc = Parse(payload);
        if (doc is null || AgentIdOf(doc.RootElement) is not { } id || _runs.FirstOrDefault(r => r.Id == id) is not { } run)
        {
            return false;
        }
        if (String(doc.RootElement, "agent_transcript_path") is { Length: > 0 } path && run.TranscriptPath is null)
        {
            run.TranscriptPath = path;
        }
        // A turn end that overtook this signal marked it interrupted: it did finish.
        if (run.State is SubagentState.Done or SubagentState.Failed)
        {
            return false;
        }
        run.State = SubagentState.Done;
        run.EndedAt = now;
        // Read once more for what it did last, even when an interruption already gave it its last read.
        run.Settled = false;
        return true;
    }

    private bool OnFailed(string payload, DateTimeOffset now)
    {
        using var doc = Parse(payload);
        if (doc is null || String(doc.RootElement, "tool_use_id") is not { } toolUseId)
        {
            return false;
        }
        // It may have failed before it started (an unknown subagent type): then there is only the launch to forget.
        _launches.RemoveAll(l => l.ToolUseId == toolUseId);
        if (_runs.FirstOrDefault(r => r.ToolUseId == toolUseId) is not { State: not SubagentState.Failed } run)
        {
            return false;
        }
        run.State = SubagentState.Failed;
        run.EndedAt ??= now;
        return true;
    }

    /// <summary>A new prompt: the finished ones of earlier turns go; what still runs in the background stays.</summary>
    private bool OnNewTurn()
    {
        _launches.Clear();
        return _runs.RemoveAll(r => r.IsFinished) > 0;
    }

    private bool Interrupt(Func<SubagentRun, bool> which, DateTimeOffset now)
    {
        var changed = false;
        foreach (var run in _runs.Where(r => r.State == SubagentState.Running && which(r)))
        {
            run.State = SubagentState.Interrupted;
            run.EndedAt = now;
            changed = true;
        }
        _launches.Clear();
        return changed;
    }

    private bool Clear()
    {
        _launches.Clear();
        if (_runs.Count == 0)
        {
            return false;
        }
        _runs.Clear();
        return true;
    }

    private static void Bind(SubagentRun run, Launch launch)
    {
        run.Description ??= launch.Description;
        run.ToolUseId ??= launch.ToolUseId;
        run.Background |= launch.Background;
    }

    /// <summary>The agent id, when it is safe to put into a file name.</summary>
    private static string? AgentIdOf(JsonElement root) =>
        String(root, "agent_id") is { Length: > 0 and <= 64 } id && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? id : null;

    private static JsonDocument? Parse(string payload)
    {
        try
        {
            var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                return doc;
            }
            doc.Dispose();
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static string? Read(string payload, string name)
    {
        using var doc = Parse(payload);
        return doc is null ? null : String(doc.RootElement, name);
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record Launch(string? ToolUseId, string? Description, string Type, bool Background);
}
