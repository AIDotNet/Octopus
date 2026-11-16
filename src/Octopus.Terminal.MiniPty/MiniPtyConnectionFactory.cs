using MiniPty;
using Octopus.Core.Terminals;

namespace Octopus.Terminal.MiniPty;

/// <summary>Starts terminal processes with MiniPty.</summary>
public sealed class MiniPtyConnectionFactory : ITerminalConnectionFactory
{
    /// <inheritdoc />
    public ITerminalConnection Start(LaunchSpec spec, int columns, int rows)
    {
        var environment = new Dictionary<string, string?>(spec.Environment.Count,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (key, value) in spec.Environment) environment[key] = value;

        // cmd parses the tail of /c as shell syntax, not CRT argv. MiniPty's argv quoting would insert
        // backslashes before its quotes, corrupting executable paths and arguments containing spaces.
        var commandLine = OperatingSystem.IsWindows()
            && string.Equals(Path.GetFileName(spec.FileName), "cmd.exe", StringComparison.OrdinalIgnoreCase)
            && spec.Arguments.Count >= 2
            && (string.Equals(spec.Arguments[^2], "/c", StringComparison.OrdinalIgnoreCase)
                || string.Equals(spec.Arguments[^2], "/k", StringComparison.OrdinalIgnoreCase))
                ? string.Join(' ', spec.Arguments.Take(spec.Arguments.Count - 1)) + " \"" + spec.Arguments[^1] + "\""
                : null;

        var session = Pty.Start(new PtyStartInfo
        {
            FileName = spec.FileName,
            Arguments = commandLine is null ? [.. spec.Arguments] : [],
            CommandLine = commandLine,
            WorkingDirectory = spec.WorkingDirectory,
            Size = new PtySize(Math.Max(1, columns), Math.Max(1, rows)),
            // The child gets exactly spec.Environment: nothing is inherited from this process.
            InheritEnvironment = false,
            Environment = environment,
            TerminalName = spec.Environment.TryGetValue("TERM", out var term) && term.Length > 0 ? term : "xterm-256color",
            // ConPTY owns console lifetime. A forced job-list attribute prevents packaged shells
            // (including Microsoft Store PowerShell) from starting with ERROR_ACCESS_DENIED.
            KillProcessTree = false,
        });
        return new MiniPtyConnection(session);
    }
}
