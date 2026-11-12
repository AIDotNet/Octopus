using System.Globalization;
using System.Text;

namespace Octopus.Ssh;

/// <summary>
/// Reads the host aliases of an OpenSSH client configuration with the options Octopus shows, the way ssh reads them:
/// <list type="bullet">
/// <item>Options before the first <c>Host</c> line apply to every host, as <c>Host *</c> does.</item>
/// <item>A <c>Host</c> block applies when one of its patterns matches the alias and none of its <c>!</c> patterns does.</item>
/// <item>For each option the first value obtained wins, across blocks and included files.</item>
/// <item><c>Include</c> is followed (relative paths are in ~/.ssh). <c>Match</c> blocks are skipped: they depend on the connection.</item>
/// </list>
/// A file that cannot be read is skipped; a damaged line is ignored. Nothing here throws for the content of a file.
/// </summary>
public static class SshConfigParser
{
    /// <summary>The nesting limit of <c>Include</c> that OpenSSH uses.</summary>
    public const int MaxIncludeDepth = 16;

    /// <summary>The concrete hosts of <paramref name="configPath"/> and its includes. <paramref name="homeDirectory"/> is what <c>~</c> stands for.</summary>
    public static IReadOnlyList<SshHost> Parse(string configPath, string homeDirectory)
    {
        var reader = new Reader(homeDirectory, Path.GetDirectoryName(configPath) ?? homeDirectory);
        reader.ReadFile(configPath, 0);
        return reader.Hosts();
    }

    /// <summary>True for a pattern with <c>*</c> or <c>?</c>: a pattern for many hosts, not a host.</summary>
    internal static bool IsWildcard(string pattern) => pattern.IndexOfAny(['*', '?']) >= 0;

    /// <summary>The ssh pattern rule: <c>*</c> is any run of characters and <c>?</c> one character. Case does not matter.</summary>
    internal static bool Matches(string pattern, string host)
    {
        var p = 0;
        var h = 0;
        var star = -1;
        var mark = 0;
        while (h < host.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(host[h])))
            {
                p++;
                h++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = h;
            }
            else if (star >= 0)
            {
                p = star + 1;
                h = ++mark;
            }
            else
            {
                return false;
            }
        }
        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }
        return p == pattern.Length;
    }

    /// <summary>The options of one <c>Host</c> line, in file order.</summary>
    private sealed record Block(IReadOnlyList<string> Positive, IReadOnlyList<string> Negative, bool Active)
    {
        public List<(string Key, string Value)> Options { get; } = [];

        public bool Applies(string host) =>
            Active && Positive.Any(p => Matches(p, host)) && !Negative.Any(n => Matches(n, host));
    }

    private sealed class Reader
    {
        private readonly string _home;
        private readonly string _sshDirectory;
        private readonly List<Block> _blocks = [];
        private Block _current;

        public Reader(string home, string sshDirectory)
        {
            _home = home;
            _sshDirectory = sshDirectory;
            _current = new Block(["*"], [], true);
            _blocks.Add(_current);
        }

        /// <summary>Reads one file into the blocks. An included file's options land in the block that was open at its Include line.</summary>
        public void ReadFile(string path, int depth)
        {
            if (depth > MaxIncludeDepth)
            {
                return;
            }
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }

            var outer = _current;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }
                var (keyword, args) = SplitLine(line);
                var key = keyword.ToLowerInvariant();
                switch (key)
                {
                    case "host":
                        _current = AddBlock(args);
                        break;
                    case "match":
                        // Depends on the connection (user, address, exec...): its options are never used.
                        _current = new Block([], [], false);
                        break;
                    case "include":
                        foreach (var pattern in args)
                        {
                            foreach (var file in Expand(pattern))
                            {
                                ReadFile(file, depth + 1);
                            }
                        }
                        break;
                    default:
                        if (args.Count > 0)
                        {
                            _current.Options.Add((key, args[0]));
                        }
                        break;
                }
            }
            _current = outer;
        }

        /// <summary>The concrete aliases in file order, each with its effective options.</summary>
        public IReadOnlyList<SshHost> Hosts()
        {
            var aliases = new List<string>();
            foreach (var pattern in _blocks.SelectMany(b => b.Positive))
            {
                // A name that starts with '-' is an option to ssh, not a host.
                if (!IsWildcard(pattern)
                    && !pattern.StartsWith('-')
                    && !aliases.Contains(pattern, StringComparer.OrdinalIgnoreCase)
                    && _blocks.Any(b => b.Applies(pattern)))
                {
                    aliases.Add(pattern);
                }
            }
            return aliases.Select(Describe).ToList();
        }

        private Block AddBlock(List<string> args)
        {
            var positive = new List<string>();
            var negative = new List<string>();
            foreach (var arg in args)
            {
                if (arg.StartsWith('!'))
                {
                    negative.Add(arg[1..]);
                }
                else
                {
                    positive.Add(arg);
                }
            }
            var block = new Block(positive, negative, true);
            _blocks.Add(block);
            return block;
        }

        private SshHost Describe(string alias) => new(
            alias,
            Value(alias, "hostname") ?? alias,
            Value(alias, "user"),
            Port(alias),
            Value(alias, "identityfile"),
            Value(alias, "proxyjump"));

        /// <summary>The first value of an option among the blocks that apply to <paramref name="host"/>.</summary>
        private string? Value(string host, string key) =>
            _blocks.Where(b => b.Applies(host))
                .SelectMany(b => b.Options)
                .Where(o => o.Key == key)
                .Select(o => (string?)o.Value)
                .FirstOrDefault();

        private int? Port(string host) =>
            int.TryParse(Value(host, "port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535
                ? port
                : null;

        /// <summary>The files an Include pattern names, in name order. A wildcard only in the file name is supported, as in the common layouts.</summary>
        private IEnumerable<string> Expand(string pattern)
        {
            try
            {
                var path = ExpandHome(pattern);
                if (!Path.IsPathRooted(path))
                {
                    path = Path.Combine(_sshDirectory, path);
                }
                if (!IsWildcard(path))
                {
                    return File.Exists(path) ? [path] : [];
                }
                var directory = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(directory) || IsWildcard(directory) || !Directory.Exists(directory))
                {
                    return [];
                }
                return Directory.GetFiles(directory, Path.GetFileName(path)).Order(StringComparer.Ordinal).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return [];
            }
        }

        private string ExpandHome(string path) =>
            path == "~" ? _home
            : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(_home, path[2..])
            : path;
    }

    /// <summary>The keyword and its arguments. The keyword ends at a space or at '='; quotes group an argument.</summary>
    internal static (string Keyword, List<string> Args) SplitLine(string line)
    {
        var end = 0;
        while (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] != '=')
        {
            end++;
        }
        var rest = line[end..].TrimStart();
        if (rest.StartsWith('='))
        {
            rest = rest[1..].TrimStart();
        }
        return (line[..end], Arguments(rest));
    }

    internal static List<string> Arguments(string text)
    {
        var args = new List<string>();
        var word = new StringBuilder();
        var inQuotes = false;
        var hasWord = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasWord = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasWord)
                {
                    args.Add(word.ToString());
                    word.Clear();
                    hasWord = false;
                }
            }
            else
            {
                word.Append(c);
                hasWord = true;
            }
        }
        if (hasWord)
        {
            args.Add(word.ToString());
        }
        return args;
    }
}
