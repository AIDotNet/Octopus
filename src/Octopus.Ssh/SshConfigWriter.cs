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
        if (draft.Protocol != SshHostProtocol.Ssh)
        {
            yield return Indent + MarkerLine(draft.Protocol);
        }
        foreach (var (key, value) in ManagedOptions(draft))
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return Indent + key + " " + Quote(value.Trim());
            }
        }
    }

    private const string Indent = "    ";

    private static string MarkerLine(SshHostProtocol protocol) =>
        "# " + SshConfigParser.ProtocolMarker + protocol.ToString().ToLowerInvariant();

    /// <summary>The options the app writes for a draft. A telnet host has no use for the others, so they are not written (and an edit leaves any that exist alone).</summary>
    private static (string Key, string? Value)[] ManagedOptions(SshHostDraft draft) => draft.Protocol == SshHostProtocol.Telnet
        ? [("HostName", draft.HostName), ("Port", draft.Port)]
        : [("HostName", draft.HostName), ("User", draft.User), ("Port", draft.Port), ("IdentityFile", draft.IdentityFile), ("ProxyJump", draft.ProxyJump)];

    /// <summary>
    /// True when the text has a block of its own for the alias: a <c>Host</c> line with that one name. Only such a block
    /// can be changed or removed without touching the other hosts.
    /// </summary>
    public static bool HasBlock(string config, string alias) => FindBlock(config.Split('\n'), alias) is not null;

    /// <summary>
    /// The text with the host's block changed to the draft's fields: the options the app manages are replaced, added or
    /// removed in place, and every other line of the block (other options, comments) stays. Returns null when there is no
    /// block of its own for <paramref name="alias"/>. The alias of the block becomes the draft's.
    /// </summary>
    public static string? Update(string config, string alias, SshHostDraft draft)
    {
        var lines = config.Split('\n').ToList();
        if (FindBlock(lines, alias) is not { } block)
        {
            return null;
        }
        var cr = config.Contains("\r\n", StringComparison.Ordinal) ? "\r" : "";
        var header = lines[block.Header];
        lines[block.Header] = header[..(header.Length - header.TrimStart().Length)] + "Host " + draft.Alias.Trim() + cr;

        // Work on the lines after the header, back to front so that the indexes stay right.
        var end = block.End;
        var indent = Indent;
        for (var i = block.Header + 1; i < end; i++)
        {
            if (IsOption(lines[i]))
            {
                indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                break;
            }
        }

        // The old marker goes; the new one (if any) is put right after the header.
        for (var i = end - 1; i > block.Header; i--)
        {
            if (SshConfigParser.ProtocolOf(lines[i].Trim()) is not null && lines[i].TrimStart().StartsWith('#'))
            {
                lines.RemoveAt(i);
                end--;
            }
        }
        var insertAt = block.Header + 1;
        if (draft.Protocol != SshHostProtocol.Ssh)
        {
            lines.Insert(insertAt, indent + MarkerLine(draft.Protocol) + cr);
            end++;
        }

        foreach (var (key, value) in ManagedOptions(draft))
        {
            var found = new List<int>();
            for (var i = block.Header + 1; i < end; i++)
            {
                if (IsOption(lines[i]) && SshConfigParser.SplitLine(lines[i].Trim()).Keyword.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(i);
                }
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                for (var k = found.Count - 1; k >= 0; k--)
                {
                    lines.RemoveAt(found[k]);
                    end--;
                }
            }
            else if (found.Count > 0)
            {
                // ssh takes the first value of an option, so that is the line that changes.
                var first = lines[found[0]];
                lines[found[0]] = first[..(first.Length - first.TrimStart().Length)] + key + " " + Quote(value.Trim()) + cr;
            }
            else
            {
                // After the block's last option (or its marker/header when it has none).
                var after = block.Header + 1;
                for (var i = block.Header + 1; i < end; i++)
                {
                    if (IsOption(lines[i]) || lines[i].TrimStart().StartsWith('#') && SshConfigParser.ProtocolOf(lines[i].Trim()) is not null)
                    {
                        after = i + 1;
                    }
                }
                lines.Insert(after, indent + key + " " + Quote(value.Trim()) + cr);
                end++;
            }
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// The text without the host's block (its header, options and marker; comments around it stay), or null when there is
    /// no block of its own for the alias.
    /// </summary>
    public static string? Remove(string config, string alias)
    {
        var lines = config.Split('\n').ToList();
        if (FindBlock(lines, alias) is not { } block)
        {
            return null;
        }
        lines.RemoveRange(block.Header, block.End - block.Header);
        // The blank line that separated the block from what followed would now be doubled.
        if (block.Header < lines.Count - 1 && lines[block.Header].Trim().Length == 0 && (block.Header == 0 || lines[block.Header - 1].Trim().Length == 0))
        {
            lines.RemoveAt(block.Header);
        }
        return string.Join("\n", lines);
    }

    /// <param name="Header">The index of the <c>Host</c> line.</param>
    /// <param name="End">The index after the block's last option or marker line.</param>
    private readonly record struct Block(int Header, int End);

    private static Block? FindBlock(IReadOnlyList<string> lines, string alias)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Trim();
            if (text.Length == 0 || text[0] == '#')
            {
                continue;
            }
            var (keyword, args) = SshConfigParser.SplitLine(text);
            if (!keyword.Equals("host", StringComparison.OrdinalIgnoreCase) || args.Count != 1 || !args[0].Equals(alias, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var end = i + 1;
            for (var j = i + 1; j < lines.Count; j++)
            {
                var next = lines[j].Trim();
                if (next.Length == 0)
                {
                    continue;
                }
                if (next[0] == '#')
                {
                    if (SshConfigParser.ProtocolOf(next) is not null)
                    {
                        end = j + 1;
                    }
                    continue;
                }
                var key = SshConfigParser.SplitLine(next).Keyword;
                if (key.Equals("host", StringComparison.OrdinalIgnoreCase) || key.Equals("match", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                end = j + 1;
            }
            return new Block(i, end);
        }
        return null;
    }

    /// <summary>A line with an option: not blank, not a comment.</summary>
    private static bool IsOption(string line)
    {
        var text = line.Trim();
        return text.Length > 0 && text[0] != '#';
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
