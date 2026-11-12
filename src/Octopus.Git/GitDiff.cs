using System.Globalization;

namespace Octopus.Git;

public enum GitDiffLineKind
{
    /// <summary>A <c>@@ -a,b +c,d @@</c> header.</summary>
    Hunk,
    Context,
    Added,
    Removed,
    /// <summary>A remark of git such as "\ No newline at end of file".</summary>
    Note
}

/// <param name="OldNumber">Line number in the old file; null for added lines, hunk headers and notes.</param>
/// <param name="NewNumber">Line number in the new file; null for removed lines, hunk headers and notes.</param>
/// <param name="Text">The line without its leading +, - or space (the whole header for a hunk).</param>
public sealed record GitDiffLine(GitDiffLineKind Kind, int? OldNumber, int? NewNumber, string Text);

public enum GitFileDiffKind
{
    /// <summary>Text with lines to show (possibly cut: <see cref="GitFileDiff.IsTruncated"/>).</summary>
    Text,
    /// <summary>The file is binary: nothing to show but the fact.</summary>
    Binary,
    /// <summary>The file or its diff is too big to be shown.</summary>
    TooLarge,
    /// <summary>No difference (the change is gone, or only a mode or an empty file).</summary>
    Empty
}

/// <summary>The diff of one file, ready to show.</summary>
/// <param name="Lines">Shown lines; at most <see cref="GitDiffParser.MaxLines"/>.</param>
/// <param name="TotalLines">Lines of the whole diff (more than <paramref name="Lines"/> when cut).</param>
/// <param name="Added">Added lines in the whole diff.</param>
/// <param name="Removed">Removed lines in the whole diff.</param>
public sealed record GitFileDiff(
    GitFileDiffKind Kind,
    IReadOnlyList<GitDiffLine> Lines,
    int TotalLines = 0,
    int Added = 0,
    int Removed = 0)
{
    public bool IsTruncated => Lines.Count < TotalLines;

    public static GitFileDiff Of(GitFileDiffKind kind) => new(kind, []);
}

/// <summary>Reads the output of <c>git diff</c> (one file) into lines with numbers.</summary>
public static class GitDiffParser
{
    /// <summary>More lines than this are counted but not kept: the view would crawl.</summary>
    public const int MaxLines = 5000;

    /// <summary>A longer line is cut (minified files): one line may not freeze the layout.</summary>
    public const int MaxLineLength = 1000;

    public static GitFileDiff Parse(string output)
    {
        var lines = new List<GitDiffLine>();
        var total = 0;
        var added = 0;
        var removed = 0;
        var oldNumber = 0;
        var newNumber = 0;
        var inHunk = false;

        void Add(GitDiffLine line)
        {
            total++;
            if (lines.Count < MaxLines) lines.Add(line);
        }

        var start = 0;
        while (start < output.Length)
        {
            var end = output.IndexOf('\n', start);
            if (end < 0) end = output.Length;
            var text = output.AsSpan(start, end - start);
            if (text.Length > 0 && text[^1] == '\r') text = text[..^1];
            start = end + 1;

            if (text.StartsWith("@@"))
            {
                inHunk = true;
                ParseHunkHeader(text, out oldNumber, out newNumber);
                Add(new GitDiffLine(GitDiffLineKind.Hunk, null, null, Cut(text)));
                continue;
            }
            if (!inHunk)
            {
                // diff --git, index, ---, +++, mode and rename lines: the tab shows the file name already.
                if (text.StartsWith("Binary files") || text.StartsWith("GIT binary patch")) return GitFileDiff.Of(GitFileDiffKind.Binary);
                continue;
            }
            if (text.StartsWith("diff --git")) { inHunk = false; continue; }
            if (text.Length == 0)
            {
                // A context line that is empty has its single space; a bare empty line only ends the output.
                if (start >= output.Length) break;
                Add(new GitDiffLine(GitDiffLineKind.Context, oldNumber++, newNumber++, ""));
                continue;
            }
            switch (text[0])
            {
                case '+':
                    added++;
                    Add(new GitDiffLine(GitDiffLineKind.Added, null, newNumber++, Cut(text[1..])));
                    break;
                case '-':
                    removed++;
                    Add(new GitDiffLine(GitDiffLineKind.Removed, oldNumber++, null, Cut(text[1..])));
                    break;
                case '\\':
                    Add(new GitDiffLine(GitDiffLineKind.Note, null, null, text.ToString()));
                    break;
                default:
                    Add(new GitDiffLine(GitDiffLineKind.Context, oldNumber++, newNumber++, Cut(text.Length > 0 ? text[1..] : text)));
                    break;
            }
        }
        return total == 0 ? GitFileDiff.Of(GitFileDiffKind.Empty) : new GitFileDiff(GitFileDiffKind.Text, lines, total, added, removed);
    }

    /// <summary>The diff of a file that is new to git: every line added.</summary>
    public static GitFileDiff AllAdded(string content)
    {
        if (content.Length == 0) return GitFileDiff.Of(GitFileDiffKind.Empty);
        var all = content.Split('\n');
        var count = all.Length - (content.EndsWith('\n') ? 1 : 0);
        var lines = new List<GitDiffLine>(Math.Min(count, MaxLines) + 2)
        {
            new(GitDiffLineKind.Hunk, null, null, $"@@ -0,0 +1,{count.ToString(CultureInfo.InvariantCulture)} @@"),
        };
        for (var i = 0; i < count && lines.Count < MaxLines; i++)
        {
            lines.Add(new GitDiffLine(GitDiffLineKind.Added, null, i + 1, Cut(all[i].TrimEnd('\r'))));
        }
        return new GitFileDiff(GitFileDiffKind.Text, lines, count + 1, count, 0);
    }

    private static string Cut(ReadOnlySpan<char> text) =>
        text.Length <= MaxLineLength ? text.ToString() : string.Concat(text[..MaxLineLength], "…");

    private static void ParseHunkHeader(ReadOnlySpan<char> header, out int oldStart, out int newStart)
    {
        oldStart = 1;
        newStart = 1;
        // @@ -12,5 +14,7 @@ section
        var minus = header.IndexOf('-');
        var plus = header.IndexOf('+');
        if (minus < 0 || plus < 0) return;
        oldStart = ReadNumber(header[(minus + 1)..]);
        newStart = ReadNumber(header[(plus + 1)..]);
    }

    private static int ReadNumber(ReadOnlySpan<char> text)
    {
        var length = 0;
        while (length < text.Length && char.IsAsciiDigit(text[length])) length++;
        return int.TryParse(text[..length], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 1;
    }
}
