using System.Text.RegularExpressions;

namespace Octopus.Updates;

/// <summary>
/// Makes the notes GitHub generates for a release readable in a small dialog: no "What's Changed" heading (the dialog
/// already says what it is), a pull request as its title and a short link instead of "by @who in https://…", and the
/// full-changelog line as a link. Notes written by hand pass through untouched. Notes that say nothing but the
/// full-changelog link come out empty, so the dialog leaves that release out instead of showing a bare heading.
/// </summary>
internal static partial class ReleaseNotesTidy
{
    public static string Apply(string notes)
    {
        var lines = new List<string>();
        foreach (var raw in notes.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (WhatsChanged().IsMatch(line)) continue;
            line = PullRequest().Replace(line, m => $"{m.Groups["lead"].Value}{m.Groups["title"].Value.TrimEnd()} ([#{m.Groups["number"].Value}]({m.Groups["url"].Value}))");
            line = FullChangelog().Replace(line, m => $"[Full changelog]({m.Groups["url"].Value})");
            lines.Add(line);
        }
        var tidy = string.Join('\n', lines).Trim();
        return lines.All(l => l.Length == 0 || ChangelogLink().IsMatch(l)) ? "" : tidy;
    }

    [GeneratedRegex(@"^\[Full changelog\]\(\S+\)$")]
    private static partial Regex ChangelogLink();

    [GeneratedRegex(@"^#{1,6}\s*What['’]s Changed\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex WhatsChanged();

    /// <summary>"* Title by @user in https://github.com/o/r/pull/12" (the bullet may also be "-").</summary>
    [GeneratedRegex(@"^(?<lead>\s*[*-]\s+)(?<title>.+?)\s+by\s+@\S+\s+in\s+(?<url>https://github\.com/[^\s/]+/[^\s/]+/pull/(?<number>\d+))\s*$")]
    private static partial Regex PullRequest();

    [GeneratedRegex(@"^\*\*Full Changelog\*\*:\s*(?<url>https://\S+)\s*$")]
    private static partial Regex FullChangelog();
}
