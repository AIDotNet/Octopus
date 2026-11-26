using System.Text;
using System.Text.RegularExpressions;

namespace Octopus.Mobile.Voice;

/// <summary>
/// The words of a conversation a recognizer would get wrong without a hint: names of files, classes and methods,
/// and the project and sessions one talks about. Plain words are recognized well already; code is not.
/// </summary>
public static partial class VoiceHotwords
{
    /// <summary>At most this many go to an engine; each engine takes fewer if it must.</summary>
    public const int Max = 40;
    private const int MaxLength = 40;

    /// <param name="names">Names to expect as they are: the project, sessions and agents one may @-mention.</param>
    /// <param name="paths">Files the agent read or changed, newest first: their names.</param>
    /// <param name="texts">What was said, newest first: the code-like words in it.</param>
    public static IReadOnlyList<string> Collect(IEnumerable<string?> names, IEnumerable<string?> paths, IEnumerable<string?> texts)
    {
        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? word)
        {
            var text = word?.Trim().Trim('.', '/', '-', '`', '"', '\'', '(', ')', ',', ':', ';');
            if (words.Count >= Max || string.IsNullOrEmpty(text) || text.Length < 2 || text.Length > MaxLength || text.Any(char.IsControl)) return;
            if (seen.Add(text)) words.Add(text);
        }

        foreach (var name in names) Add(name);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var file = Path.GetFileName(path.Trim().TrimEnd('/', '\\'));
            Add(file);
            if (Path.GetFileNameWithoutExtension(file) is { } stem && IsCodeLike(stem)) Add(stem);
        }
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (Match match in Identifier().Matches(text))
            {
                if (!IsCodeLike(match.Value)) continue;
                Add(match.Value);
                // "ConversationViewModel.cs" also as "ConversationViewModel": people say the one, files show the other.
                var dot = match.Value.IndexOf('.', StringComparison.Ordinal);
                if (dot > 0 && IsCodeLike(match.Value[..dot])) Add(match.Value[..dot]);
            }
        }
        return words;
    }

    /// <summary>Capitals inside the word, underscores, dots between letters, or letters with digits: code, not prose.</summary>
    public static bool IsCodeLike(string word) =>
        word.Length >= 3
        && (word.Skip(1).Any(char.IsUpper)
            || word.Contains('_', StringComparison.Ordinal)
            || (word.Contains('.', StringComparison.Ordinal) && word.Any(char.IsLetter))
            || (word.Any(char.IsAsciiDigit) && word.Any(char.IsAsciiLetter)));

    /// <summary>The words that fit in <paramref name="maxChars"/>, separated by spaces.</summary>
    public static string Joined(IReadOnlyList<string> words, int maxChars)
    {
        var text = new StringBuilder();
        foreach (var word in words)
        {
            if (text.Length + word.Length + 1 > maxChars) break;
            if (text.Length > 0) text.Append(' ');
            text.Append(word);
        }
        return text.ToString();
    }

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*(?:[./\-][A-Za-z0-9_]+)*")]
    private static partial Regex Identifier();
}
