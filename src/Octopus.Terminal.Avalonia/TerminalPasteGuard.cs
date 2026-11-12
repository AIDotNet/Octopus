namespace Octopus.Terminal.Avalonia;

/// <summary>What the paste confirmation shows: how many lines, and the first few.</summary>
/// <param name="LineCount">Number of lines in the pasted text.</param>
/// <param name="Lines">The first lines, long ones cut.</param>
/// <param name="MoreLines">How many lines are not in <paramref name="Lines"/>.</param>
public sealed record PastePreview(int LineCount, IReadOnlyList<string> Lines, int MoreLines);

/// <summary>Decides when a paste needs a confirmation and builds its preview.</summary>
public static class TerminalPasteGuard
{
    /// <summary>
    /// True when the text has more than one line, confirmation is switched on, and the program did not enable
    /// bracketed paste (under bracketed paste the program knows it is a paste, so it is safe).
    /// </summary>
    public static bool NeedsConfirmation(string text, bool bracketedPaste, bool confirmMultiline) =>
        confirmMultiline && !bracketedPaste && CountLines(text) > 1;

    /// <summary>Number of lines; one trailing line break does not start a new line.</summary>
    public static int CountLines(string text)
    {
        if (text.Length == 0) return 0;
        var count = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\n' && c != '\r') continue;
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            if (i + 1 < text.Length) count++;
        }
        return count;
    }

    /// <summary>The first <paramref name="maxLines"/> lines, each cut at <paramref name="maxColumns"/> characters.</summary>
    public static PastePreview Preview(string text, int maxLines = 6, int maxColumns = 100)
    {
        var all = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).ToList();
        if (all.Count > 1 && all[^1].Length == 0) all.RemoveAt(all.Count - 1);
        var shown = all.Take(maxLines)
            .Select(l => l.Length > maxColumns ? l[..maxColumns] + "…" : l)
            .ToList();
        return new PastePreview(all.Count, shown, Math.Max(0, all.Count - shown.Count));
    }
}
