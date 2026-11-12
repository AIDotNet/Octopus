namespace Octopus.FileSystem;

/// <summary>
/// A glob over relative paths, as written in manifests (<c>**/*.md</c>, <c>docs/**</c>, <c>projects/**/*.jsonl</c>).
/// <c>**</c> as a whole segment matches any number of segments (also none), <c>*</c> any characters within one
/// segment, <c>?</c> one character within one segment; everything else is literal. A pattern with a separator is
/// anchored at the root it is relative to (<c>docs/*.md</c> does not match <c>a/docs/x.md</c>); a pattern without one
/// matches the name at any depth, as in <c>.gitignore</c> and the plugin selectors (<c>*.md</c> is <c>**/*.md</c>).
/// Both <c>/</c> and <c>\</c> separate segments, in patterns and in paths.
/// </summary>
public sealed class PathGlob
{
    private const string AnySegments = "**";

    private readonly string[] _segments;
    private readonly StringComparison _comparison;

    public PathGlob(string pattern, bool ignoreCase)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        Pattern = pattern;
        IgnoreCase = ignoreCase;
        _comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var segments = Split(pattern);
        // "*.md" means any file named like that, anywhere below the root; "/x" or "docs/x" stay anchored.
        var anywhere = segments.Length == 1 && segments[0] != AnySegments && pattern.AsSpan().IndexOfAny('/', '\\') < 0;
        _segments = anywhere ? [AnySegments, segments[0]] : segments;
    }

    public string Pattern { get; }

    public bool IgnoreCase { get; }

    /// <summary>Case rules of the platform's usual file system: insensitive on macOS and Windows, sensitive elsewhere.</summary>
    public static bool PlatformIgnoresCase => OperatingSystem.IsMacOS() || OperatingSystem.IsWindows();

    /// <summary>Whether <paramref name="relativePath"/> (no leading separator) matches the whole pattern.</summary>
    public bool IsMatch(string relativePath)
    {
        var path = Split(relativePath);
        // Wildcard matching at segment level: "**" plays the part of "*", a segment pattern the part of a character.
        // Greedy with back-tracking to the last "**" is complete for patterns with only that one kind of star.
        int p = 0, s = 0, star = -1, starPath = 0;
        while (s < path.Length)
        {
            if (p < _segments.Length && _segments[p] == AnySegments)
            {
                star = p++;
                starPath = s;
            }
            else if (p < _segments.Length && SegmentMatches(_segments[p], path[s]))
            {
                p++;
                s++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                s = ++starPath;
            }
            else
            {
                return false;
            }
        }
        while (p < _segments.Length && _segments[p] == AnySegments) p++;
        return p == _segments.Length;
    }

    /// <summary>
    /// Whether something below the directory <paramref name="relativeDirectory"/> could match: false only when the
    /// pattern certainly cannot reach inside it, so a walk can skip it. True for the root itself (empty path) unless
    /// the pattern is empty.
    /// </summary>
    public bool CouldMatchInside(string relativeDirectory)
    {
        var path = Split(relativeDirectory);
        for (var i = 0; i < path.Length; i++)
        {
            if (i >= _segments.Length) return false;
            if (_segments[i] == AnySegments) return true;
            if (!SegmentMatches(_segments[i], path[i])) return false;
        }
        // Every directory segment matched; the pattern must still have something left for the entries inside.
        return _segments.Length > path.Length;
    }

    public override string ToString() => Pattern;

    private static string[] Split(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>One segment against one name: <c>*</c> and <c>?</c> never cross a separator because the input is a single segment.</summary>
    private bool SegmentMatches(string pattern, string name)
    {
        if (pattern.AsSpan().IndexOfAny('*', '?') < 0) return string.Equals(pattern, name, _comparison);
        int p = 0, n = 0, star = -1, starName = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                starName = n;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || CharEquals(pattern[p], name[n])))
            {
                p++;
                n++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++starName;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private bool CharEquals(char a, char b) =>
        a == b || (IgnoreCase && char.ToUpperInvariant(a) == char.ToUpperInvariant(b));
}
