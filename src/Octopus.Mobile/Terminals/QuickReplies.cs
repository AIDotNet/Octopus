using System.Text.RegularExpressions;
using TKCode.Terminal.Core;

namespace Octopus.Mobile.Terminals;

/// <summary>One row above the composer that answers an agent's question.</summary>
/// <param name="Keys">What it types into the terminal.</param>
/// <param name="Key">A key to press instead of typing <paramref name="Keys"/> (Esc is encoded by the terminal's mode).</param>
public sealed record QuickReply(string Label, string Keys, TerminalKey? Key = null);

/// <summary>
/// Finds the numbered choices an agent shows when it asks ("❯ 1. Yes / 2. Yes, don't ask again / 3. No") in the text
/// of the screen, so the phone can offer them as rows to tap instead of a keyboard.
/// </summary>
public static partial class QuickReplies
{
    /// <summary>Only the end of the screen counts: a question sits at the bottom.</summary>
    private const int LinesRead = 16;
    private const int MaxLabel = 44;

    [GeneratedRegex(@"^\s*(?:[❯>›→]\s*)?(\d)[.)]\s+(.+?)\s*$")]
    private static partial Regex Option();

    [GeneratedRegex(@"\s*\((?:esc|tab|enter)[^)]*\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex KeyHint();

    /// <summary>
    /// The choices of the last numbered list on the screen, when it starts at 1 and has at least two entries; otherwise
    /// nothing (a single "1." is far more often a line of ordinary text than a question).
    /// </summary>
    public static IReadOnlyList<QuickReply> Parse(string screenText)
    {
        var lines = screenText.Split('\n');
        var replies = new List<QuickReply>();
        for (var i = Math.Max(0, lines.Length - LinesRead); i < lines.Length; i++)
        {
            var match = Option().Match(lines[i].TrimEnd('\r'));
            if (!match.Success)
            {
                continue;
            }
            var number = match.Groups[1].Value[0] - '0';
            // A list that starts over is another question further down: the earlier one is gone.
            if (number == 1) replies.Clear();
            if (number != replies.Count + 1)
            {
                replies.Clear();
                continue;
            }
            replies.Add(new QuickReply(number + ". " + Shorten(KeyHint().Replace(match.Groups[2].Value, "")), match.Groups[1].Value));
        }
        return replies.Count >= 2 ? replies : [];
    }

    // The whole choice, so that "Yes" and "Yes, and don't ask again" stay apart; only a very long one is cut.
    private static string Shorten(string text) => text.Length <= MaxLabel ? text : text[..MaxLabel].TrimEnd() + "…";
}
