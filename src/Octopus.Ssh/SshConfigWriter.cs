namespace Octopus.Ssh;

/// <summary>Adds one Host block to a configuration text and leaves the rest of the text as it was.</summary>
internal static class SshConfigWriter
{
    /// <summary>
    /// The text with the block of <paramref name="draft"/> added. The block goes before the first host pattern that is a
    /// wildcard or negated, or the first <c>Match</c>: ssh takes the first value of each option, so the host's own options
    /// must come before the file's defaults. Without such a line the block goes at the end. Line endings are kept.
    /// </summary>
    public static string Insert(string config, SshHostDraft draft)
    {
        var newline = config.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var block = string.Join(newline, BlockLines(draft)) + newline;
        var at = GenericHeaderAt(config);
        if (at < 0)
        {
            if (config.Length == 0)
            {
                return block;
            }
            var head = config.EndsWith('\n') ? config : config + newline;
            return head + newline + block;
        }
        var before = config[..at];
        // A blank line before the block, unless the text already ends with one (or is empty).
        var normalized = before.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lead = normalized.Length == 0 || normalized.EndsWith("\n\n", StringComparison.Ordinal) ? "" : newline;
        return before + lead + block + newline + config[at..];
    }

    private static IEnumerable<string> BlockLines(SshHostDraft draft)
    {
        yield return "Host " + draft.Alias.Trim();
        var options = new[]
        {
            ("HostName", draft.HostName),
            ("User", draft.User),
            ("Port", draft.Port),
            ("IdentityFile", draft.IdentityFile),
            ("ProxyJump", draft.ProxyJump),
        };
        foreach (var (key, value) in options)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return "    " + key + " " + Quote(value.Trim());
            }
        }
    }

    /// <summary>A value with a space is quoted, as ssh reads it; the others are words already.</summary>
    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ? "\"" + value + "\"" : value;

    /// <summary>The position of the first line that starts a generic block, or -1.</summary>
    private static int GenericHeaderAt(string config)
    {
        var position = 0;
        foreach (var raw in config.Split('\n'))
        {
            if (IsGenericHeader(raw.Trim()))
            {
                return position;
            }
            position += raw.Length + 1;
        }
        return -1;
    }

    private static bool IsGenericHeader(string line)
    {
        if (line.Length == 0 || line[0] == '#')
        {
            return false;
        }
        var (keyword, args) = SshConfigParser.SplitLine(line);
        if (keyword.Equals("match", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return keyword.Equals("host", StringComparison.OrdinalIgnoreCase)
            && args.Any(a => a.StartsWith('!') || SshConfigParser.IsWildcard(a));
    }
}
