using System.Text;

namespace Octopus.Platform;

/// <summary>
/// Quotes arguments per shell dialect so that a command line typed into a terminal reaches the program unchanged.
/// The dialect depends on the shell, not on the operating system (pwsh runs on macOS too).
/// </summary>
public sealed class ShellCommandBuilder : IShellCommandBuilder
{
    public string Quote(string argument, ShellKind kind) => kind switch
    {
        ShellKind.Posix => QuotePosix(argument),
        ShellKind.Fish => QuoteFish(argument),
        ShellKind.PowerShell => QuotePowerShell(argument),
        ShellKind.Cmd => QuoteCmd(argument),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public string Build(string executable, IEnumerable<string> arguments, ShellKind kind)
    {
        var builder = new StringBuilder();
        // PowerShell treats a quoted first token as a string expression; the call operator makes it a command.
        if (kind == ShellKind.PowerShell)
            builder.Append("& ");
        builder.Append(Quote(executable, kind));
        foreach (var argument in arguments)
            builder.Append(' ').Append(Quote(argument, kind));
        return builder.ToString();
    }

    public IReadOnlyList<string> Split(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inToken = false; // distinguishes an empty quoted argument ('') from no argument
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\'')
            {
                inToken = true;
                var end = commandLine.IndexOf('\'', i + 1);
                if (end < 0) { current.Append(commandLine, i + 1, commandLine.Length - i - 1); break; }
                current.Append(commandLine, i + 1, end - i - 1);
                i = end;
            }
            else if (c == '"')
            {
                inToken = true;
                for (i++; i < commandLine.Length && commandLine[i] != '"'; i++)
                {
                    // Inside double quotes a backslash only escapes ", \, $ and `.
                    if (commandLine[i] == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] is '"' or '\\' or '$' or '`')
                        i++;
                    current.Append(commandLine[i]);
                }
            }
            else if (c == '\\' && i + 1 < commandLine.Length)
            {
                inToken = true;
                current.Append(commandLine[++i]);
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inToken) { result.Add(current.ToString()); current.Clear(); inToken = false; }
            }
            else
            {
                inToken = true;
                current.Append(c);
            }
        }
        if (inToken)
            result.Add(current.ToString());
        return result;
    }

    private static bool IsSafe(string s, string extra)
    {
        foreach (var c in s)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9') && extra.IndexOf(c) < 0)
                return false;
        }
        return true;
    }

    private static string QuotePosix(string s)
    {
        if (s.Length == 0) return "''";
        if (IsSafe(s, "_@%+=:,./-")) return s;
        return "'" + s.Replace("'", "'\\''") + "'";
    }

    private static string QuoteFish(string s)
    {
        if (s.Length == 0) return "''";
        if (IsSafe(s, "_@%+=:,./-")) return s;
        // Fish keeps single-quoted text literal except for \' and \\.
        return "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
    }

    private static string QuotePowerShell(string s)
    {
        if (s.Length == 0) return "''";
        // A comma would build an array; '=' and '@' have meaning too, so the safe set is small.
        if (IsSafe(s, "_.-/\\:+")) return s;
        var builder = new StringBuilder("'");
        foreach (var c in s)
        {
            builder.Append(c);
            // PowerShell also treats the typographic single quotes as quote characters; double each of them.
            if (c is '\'' or '‘' or '’' or '‚' or '‛')
                builder.Append(c);
        }
        return builder.Append('\'').ToString();
    }

    /// <summary>
    /// cmd.exe has no real quoting. Two layers apply: the program parses its command line with the MSVCRT rules
    /// (backslash-escaped quotes), and cmd itself interprets metacharacters, which are neutralised with <c>^</c>.
    /// Limitation: a newline cannot be passed through cmd at all; it is kept as-is and will end the command line.
    /// <c>%VAR%</c> expansion is defeated only on a best-effort basis.
    /// </summary>
    private static string QuoteCmd(string s)
    {
        if (s.Length > 0 && IsSafe(s, "_@+=:,./\\-")) return s;
        var argv = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in s)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"') argv.Append('\\', backslashes * 2 + 1).Append('"');
            else argv.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        argv.Append('\\', backslashes * 2).Append('"');

        var result = new StringBuilder();
        foreach (var c in argv.ToString())
        {
            if ("()%!^\"<>&|".IndexOf(c) >= 0) result.Append('^');
            result.Append(c);
        }
        return result.ToString();
    }
}
