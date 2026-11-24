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
        ShellKind.Cmd => ProcessCommand.QuoteBatchArgument(argument),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    public string Build(string executable, IEnumerable<string> arguments, ShellKind kind)
    {
        var builder = new StringBuilder();
        if (kind == ShellKind.PowerShell)
        {
            var args = arguments.ToList();
            // How PowerShell hands an embedded double quote to a native program depends on its version: Windows
            // PowerShell and Legacy mode drop it, Standard mode escapes it. Pin Legacy and escape the quotes for the
            // program's own (CRT) parser, so an argument like Codex's -c notify=["…"] arrives whole everywhere.
            // Only then: Legacy also drops empty arguments, which Standard mode keeps.
            if (args.Any(a => a.Contains('"')))
            {
                builder.Append("$PSNativeCommandArgumentPassing = 'Legacy'; ");
                args = args.Select(EscapeNativeQuotes).ToList();
            }
            // PowerShell treats a quoted first token as a string expression; the call operator makes it a command.
            builder.Append("& ").Append(Quote(executable, kind));
            foreach (var argument in args)
                builder.Append(' ').Append(Quote(argument, kind));
            return builder.ToString();
        }
        builder.Append(Quote(executable, kind));
        foreach (var argument in arguments)
            builder.Append(' ').Append(Quote(argument, kind));
        return builder.ToString();
    }

    public string WrapEnvironment(
        string commandLine,
        ShellKind kind,
        IReadOnlyList<string>? unsetVariables = null,
        IReadOnlyDictionary<string, string>? setVariables = null)
    {
        var unsets = unsetVariables?.Where(IsValidEnvName).Distinct(StringComparer.Ordinal).ToList() ?? [];
        var sets = setVariables?.Where(kv => IsValidEnvName(kv.Key)).ToList() ?? [];
        if (unsets.Count == 0 && sets.Count == 0)
        {
            return commandLine;
        }

        var builder = new StringBuilder();
        switch (kind)
        {
            case ShellKind.Posix:
                if (unsets.Count > 0)
                {
                    builder.Append("unset");
                    foreach (var name in unsets) builder.Append(' ').Append(name);
                    builder.Append("; ");
                }
                foreach (var (name, value) in sets)
                {
                    builder.Append("export ").Append(name).Append('=').Append(QuotePosix(value)).Append("; ");
                }
                builder.Append(commandLine);
                break;

            case ShellKind.Fish:
                foreach (var name in unsets)
                {
                    builder.Append("set -e ").Append(name).Append("; ");
                }
                foreach (var (name, value) in sets)
                {
                    builder.Append("set -gx ").Append(name).Append(' ').Append(QuoteFish(value)).Append("; ");
                }
                builder.Append(commandLine);
                break;

            case ShellKind.PowerShell:
                foreach (var name in unsets)
                {
                    builder.Append("Remove-Item ").Append(QuotePowerShell("Env:" + name)).Append(" -ErrorAction SilentlyContinue; ");
                }
                foreach (var (name, value) in sets)
                {
                    // Always quoted: on the right of '=' a bare word is run as a command, not taken as text.
                    builder.Append("$env:").Append(name).Append(" = ").Append(PowerShellLiteral(value)).Append("; ");
                }
                builder.Append(commandLine);
                break;

            case ShellKind.Cmd:
                foreach (var name in unsets)
                {
                    builder.Append("set \"").Append(name).Append("=\" & ");
                }
                foreach (var (name, value) in sets)
                {
                    builder.Append("set \"").Append(name).Append('=').Append(value.Replace("\"", "\"\"")).Append("\" & ");
                }
                builder.Append(commandLine);
                break;

            default:
                return commandLine;
        }
        return builder.ToString();
    }

    private static bool IsValidEnvName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!(name[0] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_')) return false;
        for (var i = 1; i < name.Length; i++)
        {
            if (!(name[i] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) return false;
        }
        return true;
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
        return PowerShellLiteral(s);
    }

    /// <summary>A single-quoted PowerShell string: taken literally, nothing expands.</summary>
    private static string PowerShellLiteral(string s)
    {
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
    /// Escapes each double quote for the Windows C runtime's argument parser (<c>\"</c>, doubling the backslashes
    /// before it). Legacy-mode PowerShell passes the text through as it is and wraps it in quotes when it has spaces.
    /// </summary>
    private static string EscapeNativeQuotes(string s)
    {
        if (!s.Contains('"')) return s;
        var builder = new StringBuilder(s.Length + 8);
        var backslashes = 0;
        foreach (var c in s)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
            }
            else
            {
                builder.Append('\\', backslashes);
            }
            backslashes = 0;
            builder.Append(c);
        }
        builder.Append('\\', backslashes);
        return builder.ToString();
    }
}
