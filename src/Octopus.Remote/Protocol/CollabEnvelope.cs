using System.Text;

namespace Octopus.Remote.Protocol;

/// <summary>Correlation ids and reply expectation parsed from a <see cref="CollabEnvelope"/> prompt.</summary>
/// <param name="ThreadId">The collaboration thread id, if present in the envelope header.</param>
/// <param name="TaskId">The collaboration task id, if present in the envelope header or task body prefix.</param>
/// <param name="ExpectsReply">True when this envelope expects a reply or implicit turn-end report.</param>
public readonly record struct CollabEnvelopeCorrelation(string? ThreadId, string? TaskId, bool ExpectsReply);

/// <summary>
/// The envelope the multi-agent collaboration bus wraps around every prompt it injects into a session: a first line
/// <c>[Octopus Collab … ]</c> saying what it is and who sent it, the body, and maybe an instruction footer for the agent.
/// Agents read it as text; devices and the desktop timeline parse it back to show a collaboration card. Building and
/// parsing live together here so the two cannot drift apart (desktop, phone and host all use this type).
/// </summary>
/// <param name="RawHeader">The first line without brackets and the leading "Octopus " (<c>Collab Task #task_1 · Thread #t_2 · …</c>).</param>
/// <param name="Header">The same without ids, rounds and the device (<c>Collab Task · Delegated by @Claude Code · Lead</c>).</param>
/// <param name="Body">What the sender wrote, without the footer; may be empty.</param>
/// <param name="FromDevice">The computer the work came from, when another computer's agent sent it.</param>
public sealed record CollabEnvelope(string RawHeader, string Header, string Body, string? FromDevice)
{
    private const string Prefix = "[Octopus Collab";

    // Footers for the receiving agent, each starting with a fixed text a reader can recognise and drop.
    private const string DelegationFooter = "When finished (or if you need clarification from the delegating agent),";
    private const string AgreedFooter = "(The other session marked this thread as agreed/completed;";
    private const string ReplyFooter = "(Reply via `reply_thread(";
    private const string InterjectionFooter = "(Please follow the user's guidance and continue or conclude the thread";
    private const string RemoteMessageFooter = "(This message comes from another computer:";

    private static readonly string[] FooterPrefixes = [DelegationFooter, AgreedFooter, ReplyFooter, InterjectionFooter, RemoteMessageFooter];

    /* ==================== building ==================== */

    /// <summary>The first prompt of a delegated task.</summary>
    /// <param name="fromDevice">Set when the task goes to another computer: its agent cannot reach this thread with reply_thread.</param>
    public static string Delegation(
        string taskId,
        string threadId,
        string fromLabel,
        string goal,
        IReadOnlyList<string> contextFiles,
        string? fromDevice = null,
        string? worktreeBranch = null,
        bool readOnly = false)
    {
        var sb = new StringBuilder();
        sb.Append("[Octopus Collab Task #").Append(taskId)
          .Append(" · Thread #").Append(threadId)
          .Append(" · Delegated by @").Append(fromLabel);
        if (fromDevice is not null) sb.Append(" · From device ").Append(fromDevice);
        if (worktreeBranch is not null) sb.Append(" · Git Worktree: ").Append(worktreeBranch);
        else if (readOnly) sb.Append(" · Read-Only Mode");
        sb.AppendLine("]");
        sb.AppendLine(goal.Trim());
        if (contextFiles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Relevant files:");
            foreach (var f in contextFiles) sb.Append("- ").AppendLine(f);
        }
        sb.AppendLine();
        if (fromDevice is not null)
        {
            sb.Append(DelegationFooter).Append(" conclude with a concise summary or your question. ")
              .Append("This task was delegated from another computer (").Append(fromDevice)
              .Append("); Octopus reports your final response back automatically. Do not call reply_thread for this thread.");
            return sb.ToString();
        }
        sb.Append(DelegationFooter).Append(" either call `reply_thread(thread_id=\"").Append(threadId)
          .Append("\", message=..., status=\"agreed\"|\"continue\")` or simply conclude with a concise summary — Octopus will automatically report your final response back.");
        return sb.ToString();
    }

    /// <summary>A message on a negotiation thread.</summary>
    /// <param name="toRemote">The target runs on another computer: it answers by ending its turn, not with reply_thread.</param>
    public static string Message(string threadId, string fromLabel, int round, int maxHops, string message, bool expectReply, bool toRemote = false)
    {
        var sb = new StringBuilder();
        sb.Append("[Octopus Collab · From @").Append(fromLabel)
          .Append(" · Thread #").Append(threadId)
          .Append(" · Round ").Append(round).Append('/').Append(maxHops).AppendLine("]");
        sb.AppendLine(message.Trim());
        if (expectReply && toRemote)
        {
            sb.AppendLine();
            sb.Append(RemoteMessageFooter).Append(" conclude this turn with your answer and Octopus relays it back automatically; reply_thread cannot reach this thread.)");
        }
        else if (expectReply)
        {
            sb.AppendLine();
            sb.Append(ReplyFooter).Append("thread_id=\"").Append(threadId)
              .Append("\", message=..., status=\"continue\"|\"agreed\")` or conclude this turn with your answer for automatic relay.)");
        }
        return sb.ToString();
    }

    /// <summary>A reply (or an outcome: cancelled, blocked) delivered to the session that asked.</summary>
    /// <param name="autoReportedFromSessionId">Set when the reply is the responder's last words at the end of its turn
    /// (it did not call reply_thread): the footer says how to answer if that was really a question.</param>
    public static string Reply(
        string threadId,
        string threadTitle,
        string fromLabel,
        int round,
        int maxHops,
        string status,
        string message,
        string? autoReportedFromSessionId = null)
    {
        var sb = new StringBuilder();
        sb.Append("[Octopus Collab Reply · From @").Append(fromLabel)
          .Append(" · Thread #").Append(threadId).Append(" (").Append(threadTitle).Append(')')
          .Append(" · Round ").Append(round).Append('/').Append(maxHops)
          .Append(" · Status: ").Append(status).AppendLine("]");
        sb.AppendLine(message);
        if (status == "agreed" && autoReportedFromSessionId is not null)
        {
            sb.AppendLine();
            sb.Append(AgreedFooter).Append(" this was reported automatically when its turn ended. ")
              .Append("If it is actually asking you something, answer with `send_message(target_session_id=\"").Append(autoReportedFromSessionId)
              .Append("\", thread_id=\"").Append(threadId).Append("\", message=...)` and its task reopens; otherwise summarize or integrate the result for the user.)");
        }
        else if (status == "agreed")
        {
            sb.AppendLine();
            sb.Append(AgreedFooter).Append(" summarize or integrate the result for the user unless further work is needed.)");
        }
        return sb.ToString();
    }

    /// <summary>What the user typed into a thread between agents.</summary>
    public static string Interjection(string threadId, string text) =>
        $"[Octopus Collab · User Interjection · Thread #{threadId}]\n{text.Trim()}\n{InterjectionFooter} via reply_thread.)";

    /// <summary>The body of a task handed to an existing session (it travels inside a <see cref="Message"/>).</summary>
    public static string TaskBody(string taskId, string goal, IReadOnlyList<string> contextFiles)
    {
        var sb = new StringBuilder();
        sb.Append("[Task #").Append(taskId).AppendLine("]");
        sb.AppendLine(goal.Trim());
        if (contextFiles.Count > 0) sb.AppendLine("Relevant files: " + string.Join(", ", contextFiles));
        return sb.ToString();
    }

    /* ==================== parsing ==================== */

    /// <summary>The envelope of <paramref name="text"/>, or null for an ordinary prompt.</summary>
    public static CollabEnvelope? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart();
        if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var lineEnd = trimmed.IndexOf('\n');
        var firstLine = (lineEnd >= 0 ? trimmed[..lineEnd] : trimmed).Trim();
        if (firstLine.Length <= 2 || !firstLine.EndsWith(']')) return null;

        var inner = firstLine[1..^1].Trim();
        if (inner.StartsWith("Octopus ", StringComparison.Ordinal)) inner = inner["Octopus ".Length..];

        // Ids (#task_…, Thread #t_…) and rounds mean nothing to a reader; the kind, who and where do.
        string? fromDevice = null;
        var parts = new List<string>();
        foreach (var raw in inner.Split(" · ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("Thread #", StringComparison.Ordinal) || raw.StartsWith("Round ", StringComparison.Ordinal)) continue;
            if (raw.StartsWith("From device ", StringComparison.Ordinal))
            {
                fromDevice = raw["From device ".Length..].Trim();
                continue;
            }
            var hash = raw.IndexOf(" #", StringComparison.Ordinal);
            parts.Add(hash > 0 ? raw[..hash] : raw);
        }

        var body = lineEnd >= 0 ? StripFooter(trimmed[(lineEnd + 1)..].Trim()) : "";
        return new CollabEnvelope(inner, string.Join(" · ", parts), body, fromDevice);
    }

    /// <summary>Extracts thread/task correlation metadata from a collaboration envelope, or null for an ordinary prompt.</summary>
    public static CollabEnvelopeCorrelation? TryExtractCorrelation(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart();
        if (!normalized.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var lineEnd = normalized.IndexOf('\n');
        var firstLine = (lineEnd >= 0 ? normalized[..lineEnd] : normalized).Trim();
        if (firstLine.Length <= 2 || !firstLine.EndsWith(']')) return null;

        var inner = firstLine[1..^1].Trim();
        if (inner.StartsWith("Octopus ", StringComparison.Ordinal)) inner = inner["Octopus ".Length..];

        string? threadId = null;
        string? taskId = null;
        string? replyStatus = null;
        var isDelegation = false;
        var isReply = false;
        var isInterjection = false;

        foreach (var raw in inner.Split(" · ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("Collab Task #", StringComparison.Ordinal))
            {
                isDelegation = true;
                taskId ??= ExtractToken(raw["Collab Task #".Length..]);
            }
            else if (raw.StartsWith("Collab Reply", StringComparison.Ordinal))
            {
                isReply = true;
            }
            else if (string.Equals(raw, "User Interjection", StringComparison.Ordinal))
            {
                isInterjection = true;
            }
            else if (raw.StartsWith("Thread #", StringComparison.Ordinal))
            {
                threadId ??= ExtractToken(raw["Thread #".Length..]);
            }
            else if (raw.StartsWith("Status: ", StringComparison.Ordinal))
            {
                replyStatus = ExtractToken(raw["Status: ".Length..]);
            }
        }

        var body = lineEnd >= 0 ? normalized[(lineEnd + 1)..].TrimStart() : "";
        if (taskId is null && body.StartsWith("[Task #", StringComparison.Ordinal))
        {
            var close = body.IndexOf(']');
            if (close > "[Task #".Length)
            {
                taskId = ExtractToken(body["[Task #".Length..close]);
            }
        }

        var expectsReply = isDelegation
            || (!isInterjection && isReply && string.Equals(replyStatus, "continue", StringComparison.Ordinal))
            || (!isInterjection && !isReply && (body.Contains(ReplyFooter, StringComparison.Ordinal) || body.Contains(RemoteMessageFooter, StringComparison.Ordinal)));

        return new CollabEnvelopeCorrelation(threadId, taskId, expectsReply);
    }

    private static string? ExtractToken(string value)
    {
        var span = value.AsSpan().Trim();
        if (span.IsEmpty) return null;
        var end = 0;
        while (end < span.Length && !char.IsWhiteSpace(span[end]) && span[end] is not ('(' or ')' or ']'))
        {
            end++;
        }
        return end > 0 ? span[..end].ToString() : null;
    }

    /// <summary>Drops the instruction footer for the agent, if any.</summary>
    public static string StripFooter(string body)
    {
        var normalized = body.Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var prefix in FooterPrefixes)
        {
            var at = normalized.LastIndexOf(prefix, StringComparison.Ordinal);
            if (at == 0) return "";
            if (at > 0 && normalized[at - 1] == '\n') return normalized[..at].TrimEnd();
        }
        return body;
    }
}
