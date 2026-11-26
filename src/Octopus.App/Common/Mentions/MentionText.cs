namespace Octopus.App.Common.Mentions;

/// <summary>
/// One mention inside a prompt draft: the exact text written into the prompt (for example <c>@Codex（重构登录）</c>) and
/// what it points at. The prompt text stays plain; a mention is any occurrence of <see cref="Text"/> standing on word
/// boundaries, so undo, paste, history and soft-keyboard edits never lose track of it.
/// </summary>
/// <param name="Kind"><c>session</c> (an existing session) or <c>agent</c> (an agent to start).</param>
/// <param name="TargetId">Session item id for <c>session</c>, agent id for <c>agent</c>.</param>
/// <param name="Text">The token as written in the prompt, starting with <c>@</c>.</param>
/// <param name="AgentId">Agent behind the mention, for its icon.</param>
public sealed record MentionToken(string Kind, string TargetId, string Text, string? AgentId = null);

/// <summary>Where a <see cref="MentionToken"/> sits in the current text.</summary>
public readonly record struct MentionSpan(int Start, int Length, MentionToken Token)
{
    public int End => Start + Length;

    /// <summary>True when <paramref name="index"/> is strictly inside the token (not on either edge).</summary>
    public bool ContainsInside(int index) => index > Start && index < End;
}

/// <summary>Result of <see cref="MentionText.RepairEdit"/>: the text to show and where the caret goes.</summary>
public readonly record struct MentionRepair(string Text, int Caret);

/// <summary>An <c>@query</c> being typed before the caret: <c>[Start, End)</c> covers the <c>@</c> and the query.</summary>
public readonly record struct MentionQuery(int Start, int End, string Query);

/// <summary>
/// Plain-text mention rules shared by the desktop composer and the phone composer: finding tokens, keeping them atomic
/// whatever produced an edit (keyboard, IME, soft keyboard, paste), caret snapping and building unique labels.
/// </summary>
public static class MentionText
{
    private const int MaxQueryLength = 48;
    private const int MaxDetailLength = 12;

    /// <summary>Finds every token occurrence in <paramref name="text"/>, ordered by position. Longer tokens win overlaps.</summary>
    public static IReadOnlyList<MentionSpan> Scan(string? text, IReadOnlyCollection<MentionToken>? tokens)
    {
        if (string.IsNullOrEmpty(text) || tokens is null || tokens.Count == 0) return [];

        List<MentionSpan>? found = null;
        foreach (var token in tokens.OrderByDescending(t => t.Text.Length))
        {
            if (token.Text.Length == 0) continue;
            var i = text.IndexOf(token.Text, StringComparison.Ordinal);
            while (i >= 0)
            {
                var end = i + token.Text.Length;
                if (IsBoundaryBefore(text, i) && IsBoundaryAfter(text, end) && !Overlaps(found, i, end))
                {
                    (found ??= []).Add(new MentionSpan(i, token.Text.Length, token));
                }
                i = end < text.Length ? text.IndexOf(token.Text, i + 1, StringComparison.Ordinal) : -1;
            }
        }

        if (found is null) return [];
        found.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return found;
    }

    /// <summary>
    /// Keeps tokens atomic. Given the text and tokens before an edit and the text after it, returns the repaired text when
    /// the edit removed part of a token (the rest of that token goes too), or <c>null</c> when the edit is fine as it is.
    /// Works on the text alone, so it does not matter whether the edit came from a key, an IME or a soft keyboard.
    /// </summary>
    public static MentionRepair? RepairEdit(string oldText, IReadOnlyList<MentionSpan> oldSpans, string newText)
    {
        if (oldSpans.Count == 0 || string.Equals(oldText, newText, StringComparison.Ordinal)) return null;

        var minLength = Math.Min(oldText.Length, newText.Length);
        var prefix = 0;
        while (prefix < minLength && oldText[prefix] == newText[prefix]) prefix++;
        var suffix = 0;
        while (suffix < minLength - prefix && oldText[^(suffix + 1)] == newText[^(suffix + 1)]) suffix++;

        var removedStart = prefix;
        var removedEnd = oldText.Length - suffix;
        if (removedEnd <= removedStart) return null; // pure insertion

        var start = removedStart;
        var end = removedEnd;
        foreach (var span in oldSpans)
        {
            var overlaps = span.Start < end && span.End > start;
            var partial = span.Start < start || span.End > end;
            if (overlaps && partial)
            {
                start = Math.Min(start, span.Start);
                end = Math.Max(end, span.End);
            }
        }
        if (start == removedStart && end == removedEnd) return null;

        var inserted = newText[prefix..(newText.Length - suffix)];
        return new MentionRepair(string.Concat(oldText.AsSpan(0, start), inserted, oldText.AsSpan(end)), start + inserted.Length);
    }

    /// <summary>
    /// Moves a caret that landed inside a token to one of its edges: forward one step goes to the end, back one step to the
    /// start (so arrow keys hop over tokens), anything else (a click, a tap) to the nearer edge.
    /// </summary>
    public static int SnapCaret(int caret, int previousCaret, IReadOnlyList<MentionSpan> spans)
    {
        foreach (var span in spans)
        {
            if (!span.ContainsInside(caret)) continue;
            return (caret - previousCaret) switch
            {
                1 => span.End,
                -1 => span.Start,
                _ => caret - span.Start <= span.End - caret ? span.Start : span.End,
            };
        }
        return caret;
    }

    /// <summary>
    /// The <c>@query</c> typed right before <paramref name="caret"/>, or <c>null</c>. The <c>@</c> must start the text or
    /// follow a non-word character (so e-mail addresses do not trigger it), and must not belong to an existing token.
    /// </summary>
    public static MentionQuery? FindQuery(string? text, int caret, IReadOnlyList<MentionSpan> spans)
    {
        if (string.IsNullOrEmpty(text)) return null;
        caret = Math.Clamp(caret, 0, text.Length);
        var limit = Math.Max(0, caret - MaxQueryLength - 1);
        for (var i = caret - 1; i >= limit; i--)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) return null;
            if (c != '@') continue;
            if (!IsBoundaryBefore(text, i)) return null;
            foreach (var span in spans)
            {
                if (i >= span.Start && i < span.End) return null;
            }
            return new MentionQuery(i, caret, text[(i + 1)..caret]);
        }
        return null;
    }

    /// <summary>
    /// The token text for a target: <c>@Name</c>, or <c>@Name（detail）</c> when a detail tells sessions apart. A target
    /// already in <paramref name="existing"/> keeps its text; a clash with another target gets a <c>·2</c>, <c>·3</c>… suffix.
    /// </summary>
    public static string BuildLabel(string name, string? detail, string kind, string targetId, IEnumerable<MentionToken> existing)
    {
        var tokens = existing as IReadOnlyCollection<MentionToken> ?? existing.ToList();
        foreach (var token in tokens)
        {
            if (token.Kind == kind && string.Equals(token.TargetId, targetId, StringComparison.Ordinal)) return token.Text;
        }

        var cleanName = Collapse(name);
        if (cleanName.Length == 0) cleanName = targetId;
        var cleanDetail = Collapse(detail);
        var text = cleanDetail.Length == 0 || string.Equals(cleanDetail, cleanName, StringComparison.OrdinalIgnoreCase)
            ? "@" + cleanName
            : $"@{cleanName}（{Truncate(cleanDetail, MaxDetailLength)}）";

        var candidate = text;
        for (var n = 2; tokens.Any(t => string.Equals(t.Text, candidate, StringComparison.Ordinal)); n++)
        {
            candidate = $"{text}·{n}";
        }
        return candidate;
    }

    /// <summary>
    /// Replaces <c>[start, end)</c> with <paramref name="tokenText"/> plus a trailing space (unless one follows already),
    /// adding a leading space when the token would otherwise glue onto a word. Returns the new text and caret.
    /// </summary>
    public static MentionRepair InsertToken(string text, int start, int end, string tokenText)
    {
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        var lead = start > 0 && IsWordChar(text[start - 1]) ? " " : "";
        var trail = end < text.Length && char.IsWhiteSpace(text[end]) ? "" : " ";
        var insert = lead + tokenText + trail;
        // The caret goes after the space that follows the token, whether it was just added or already there.
        return new MentionRepair(string.Concat(text.AsSpan(0, start), insert, text.AsSpan(end)), start + lead.Length + tokenText.Length + 1);
    }

    /// <summary>Distinct tokens in the order they first appear in <paramref name="spans"/>.</summary>
    public static IReadOnlyList<MentionToken> DistinctInOrder(IReadOnlyList<MentionSpan> spans)
    {
        if (spans.Count == 0) return [];
        var seen = new HashSet<(string, string)>();
        var result = new List<MentionToken>(spans.Count);
        foreach (var span in spans)
        {
            if (seen.Add((span.Token.Kind, span.Token.TargetId))) result.Add(span.Token);
        }
        return result;
    }

    private static bool Overlaps(List<MentionSpan>? spans, int start, int end)
    {
        if (spans is null) return false;
        foreach (var span in spans)
        {
            if (span.Start < end && span.End > start) return true;
        }
        return false;
    }

    private static bool IsBoundaryBefore(string text, int index) => index == 0 || !IsWordChar(text[index - 1]);

    private static bool IsBoundaryAfter(string text, int end) => end >= text.Length || !IsWordChar(text[end]);

    /// <summary>ASCII word characters only: CJK text may sit right next to a token (<c>让@Codex处理</c>).</summary>
    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static string Collapse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
