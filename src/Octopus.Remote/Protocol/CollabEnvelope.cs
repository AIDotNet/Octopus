namespace Octopus.Remote.Protocol;

/// <summary>
/// Reads the envelope the multi-agent collaboration bus wraps around prompts it injects into a session
/// (<c>[Octopus Collab Task #… · Delegated by @… · From device …]</c>, <c>[Octopus Collab Reply · From @…]</c>, …), so a
/// device shows them as collaboration cards rather than as something the person typed. Hosts send user entries verbatim;
/// this is presentation only.
/// </summary>
public sealed record CollabEnvelope(string Header, string Body, string? FromDevice)
{
    private const string Prefix = "[Octopus Collab";

    // Instruction footers the bus appends for the receiving agent; a reader does not need them.
    private static readonly string[] FooterPrefixes =
    [
        "When finished (or if you need clarification from the delegating agent),",
        "(The other session marked this thread as agreed/completed;",
        "(Reply via `reply_thread(",
        "(Please follow the user's guidance and continue or conclude the thread",
        "(This message comes from another computer:",
    ];

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

        // Ids (#task_…, Thread #t_…) mean nothing to a reader; the kind, who and where do.
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
        var header = string.Join(" · ", parts);

        var body = lineEnd >= 0 ? trimmed[(lineEnd + 1)..].Trim() : "";
        body = StripFooter(body);
        return new CollabEnvelope(header, body.Length > 0 ? body : header, fromDevice);
    }

    private static string StripFooter(string body)
    {
        foreach (var prefix in FooterPrefixes)
        {
            var at = body.LastIndexOf(prefix, StringComparison.Ordinal);
            if (at == 0) return "";
            if (at > 0 && body[at - 1] == '\n') return body[..at].TrimEnd();
        }
        return body;
    }
}
