using MiniPty;
using Octopus.Core.Terminals;

namespace Octopus.Terminal.MiniPty;

/// <summary>Starts terminal processes with MiniPty.</summary>
public sealed class MiniPtyConnectionFactory : ITerminalConnectionFactory
{
    /// <inheritdoc />
    public ITerminalConnection Start(LaunchSpec spec, int columns, int rows)
    {
        var environment = new Dictionary<string, string?>(spec.Environment.Count);
        foreach (var (key, value) in spec.Environment) environment[key] = value;

        var session = Pty.Start(new PtyStartInfo
        {
            FileName = spec.FileName,
            Arguments = [.. spec.Arguments],
            WorkingDirectory = spec.WorkingDirectory,
            Size = new PtySize(Math.Max(1, columns), Math.Max(1, rows)),
            // The child gets exactly spec.Environment: nothing is inherited from this process.
            InheritEnvironment = false,
            Environment = environment,
            TerminalName = spec.Environment.TryGetValue("TERM", out var term) && term.Length > 0 ? term : "xterm-256color",
            KillProcessTree = true,
        });
        return new MiniPtyConnection(session);
    }
}
