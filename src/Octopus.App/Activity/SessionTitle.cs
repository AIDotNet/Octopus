namespace Octopus.App.Activity;

/// <summary>
/// What a terminal title (OSC 0/2) says about the program in it. Agents put a status mark in front of the session's
/// name: Claude Code shows "✳ Fix the login test" at rest and a moving braille dot while it works, others turn a
/// quarter circle. The mark is the fallback signal for "working" (hooks come first, see <see cref="ItemActivityService"/>).
/// </summary>
/// <param name="Text">The title without its status mark; empty when the title was only a mark.</param>
/// <param name="Working">True when the mark is a spinner frame.</param>
public readonly record struct SessionTitle(string Text, bool Working)
{
    /// <summary>Marks an agent shows while it rests or waits; they are removed, and mean "not working".</summary>
    private const string RestMarks = "✳✶✻✽✢✦◇·∗*●○";

    /// <summary>Spinner frames besides the braille block: quarter and half circles.</summary>
    private const string SpinMarks = "◐◓◑◒◴◷◶◵";

    public static SessionTitle Parse(string? raw)
    {
        var text = (raw ?? "").Trim();
        var working = false;
        // A mark counts only as a prefix of its own: one or more mark characters, then a space or the end.
        var end = 0;
        while (end < text.Length && IsMark(text[end]))
        {
            end++;
        }
        if (end > 0 && (end == text.Length || char.IsWhiteSpace(text[end])))
        {
            working = text.AsSpan(0, end).IndexOfAny(SpinMarks) >= 0 || HasBraille(text.AsSpan(0, end));
            text = text[end..].Trim();
        }
        return new SessionTitle(text, working);
    }

    private static bool IsMark(char c) => IsBraille(c) || RestMarks.Contains(c) || SpinMarks.Contains(c);

    /// <summary>U+2800 is the blank braille cell: part of a spinner's alphabet, but no dot is lit.</summary>
    private static bool IsBraille(char c) => c is >= '⠀' and <= '⣿';

    private static bool HasBraille(ReadOnlySpan<char> mark)
    {
        foreach (var c in mark)
        {
            if (IsBraille(c))
            {
                return true;
            }
        }
        return false;
    }
}
