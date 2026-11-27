namespace Octopus.FileSystem;

/// <summary>
/// Lightweight, zero-process <c>.gitignore</c> rule matcher used by <see cref="WorkspaceFileIndex"/>
/// to deprioritize gitignored build/log/output directories without shelling out to <c>git</c>.
/// </summary>
internal sealed class GitIgnoreMatcher
{
    private const long MaxIgnoreFileBytes = 256 * 1024;

    public static GitIgnoreMatcher Empty { get; } = new([], null);

    private readonly Rule[] _rules;
    private readonly HashSet<string>? _fastLiteralNames;
    private readonly bool _ignoreCase;

    private GitIgnoreMatcher(Rule[] rules, HashSet<string>? fastLiteralNames)
    {
        _rules = rules;
        _fastLiteralNames = fastLiteralNames;
        _ignoreCase = PathGlob.PlatformIgnoresCase;
    }

    public bool IsEmpty => _rules.Length == 0 && _fastLiteralNames is null;

    /// <summary>Loads rules from <c>.git/info/exclude</c> and <c>.gitignore</c> under <paramref name="root"/>.</summary>
    public static GitIgnoreMatcher Load(string root)
    {
        var rules = new List<Rule>();
        TryParseFile(Path.Combine(root, ".git", "info", "exclude"), rules);
        TryParseFile(Path.Combine(root, ".gitignore"), rules);
        if (rules.Count == 0) return Empty;

        var hasNegation = false;
        for (var i = 0; i < rules.Count; i++)
        {
            if (rules[i].IsNegation)
            {
                hasNegation = true;
                break;
            }
        }

        // When there are no negation rules (the vast majority of repositories), simple unanchored literal
        // names can be checked in O(1) via a HashSet before scanning the remaining glob/anchored rules.
        if (!hasNegation)
        {
            HashSet<string>? fastLiterals = null;
            List<Rule>? remaining = null;
            var comparer = PathGlob.PlatformIgnoresCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            foreach (var rule in rules)
            {
                if (!rule.Anchored && !rule.HasWildcard && !rule.DirectoryOnly)
                {
                    (fastLiterals ??= new HashSet<string>(comparer)).Add(rule.Pattern);
                }
                else
                {
                    (remaining ??= []).Add(rule);
                }
            }
            return new GitIgnoreMatcher(remaining is null ? [] : [.. remaining], fastLiterals);
        }

        return new GitIgnoreMatcher([.. rules], null);
    }

    /// <summary>Parses raw <c>.gitignore</c> content directly (used by unit tests).</summary>
    internal static GitIgnoreMatcher Parse(string content)
    {
        var rules = new List<Rule>();
        ParseLines(content, rules);
        if (rules.Count == 0) return Empty;
        return new GitIgnoreMatcher([.. rules], null);
    }

    public bool IsIgnored(string relativePath, string name, bool isDirectory)
    {
        if (IsEmpty) return false;

        if (_fastLiteralNames is not null && _fastLiteralNames.Contains(name))
        {
            return true;
        }

        var ignored = false;
        var comparison = _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var i = 0; i < _rules.Length; i++)
        {
            ref readonly var rule = ref _rules[i];
            if (rule.DirectoryOnly && !isDirectory) continue;

            var matched = rule.Anchored
                ? MatchesAnchored(rule, relativePath.AsSpan(), comparison)
                : MatchesUnanchored(rule, name.AsSpan(), comparison);

            if (matched)
            {
                ignored = !rule.IsNegation;
            }
        }
        return ignored;
    }

    private static void TryParseFile(string path, List<Rule> rules)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxIgnoreFileBytes) return;
            ParseLines(File.ReadAllText(path), rules);
        }
        catch (Exception)
        {
            // Ignore unreadable ignore files.
        }
    }

    private static void ParseLines(string content, List<Rule> rules)
    {
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } raw)
        {
            var line = TrimTrailingUnescapedSpaces(raw);
            if (line.Length == 0 || line[0] == '#') continue;

            var isNegation = false;
            if (line[0] == '!')
            {
                isNegation = true;
                line = line[1..];
                if (line.Length == 0) continue;
            }
            else if (line.StartsWith("\\#", StringComparison.Ordinal) || line.StartsWith("\\!", StringComparison.Ordinal))
            {
                line = line[1..];
            }

            line = line.Replace('\\', '/');

            var directoryOnly = false;
            if (line.EndsWith('/'))
            {
                directoryOnly = true;
                line = line.TrimEnd('/');
                if (line.Length == 0) continue;
            }

            var anchored = false;
            if (line[0] == '/')
            {
                anchored = true;
                line = line.TrimStart('/');
                if (line.Length == 0) continue;
            }

            if (line.Contains('/')) anchored = true;

            var hasWildcard = line.AsSpan().IndexOfAny('*', '?') >= 0;
            var glob = anchored && hasWildcard ? new PathGlob(line, PathGlob.PlatformIgnoresCase) : null;
            rules.Add(new Rule(line, isNegation, directoryOnly, anchored, hasWildcard, glob));
        }
    }

    private static string TrimTrailingUnescapedSpaces(string line)
    {
        var end = line.Length;
        while (end > 0 && line[end - 1] is ' ' or '\t')
        {
            if (end >= 2 && line[end - 2] == '\\')
            {
                break;
            }
            end--;
        }
        return end == line.Length ? line : line[..end];
    }

    private bool MatchesUnanchored(in Rule rule, ReadOnlySpan<char> name, StringComparison comparison)
    {
        if (!rule.HasWildcard)
        {
            return name.Equals(rule.Pattern.AsSpan(), comparison);
        }
        return WildcardMatches(rule.Pattern.AsSpan(), name, _ignoreCase);
    }

    private static bool MatchesAnchored(in Rule rule, ReadOnlySpan<char> relativePath, StringComparison comparison)
    {
        if (rule.Glob is not null)
        {
            return rule.Glob.IsMatch(relativePath.ToString());
        }
        return relativePath.Equals(rule.Pattern.AsSpan(), comparison);
    }

    private static bool WildcardMatches(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text, bool ignoreCase)
    {
        int p = 0, t = 0, star = -1, starText = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                starText = t;
            }
            else if (p < pattern.Length && (pattern[p] == '?' || CharEquals(pattern[p], text[t], ignoreCase)))
            {
                p++;
                t++;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++starText;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static bool CharEquals(char a, char b, bool ignoreCase) =>
        a == b || (ignoreCase && char.ToUpperInvariant(a) == char.ToUpperInvariant(b));

    private readonly record struct Rule(
        string Pattern,
        bool IsNegation,
        bool DirectoryOnly,
        bool Anchored,
        bool HasWildcard,
        PathGlob? Glob);
}
