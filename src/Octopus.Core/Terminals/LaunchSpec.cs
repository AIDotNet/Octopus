namespace Octopus.Core.Terminals;

/// <summary>Exactly what a pty starts.</summary>
/// <param name="Environment">The complete environment of the child process.</param>
public sealed record LaunchSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

/// <param name="ExitCode">Null when the process was ended by a signal.</param>
public readonly record struct TerminalExit(int? ExitCode, int? Signal);

public enum TerminalSignal
{
    Interrupt,
    Hangup,
    Terminate,
    Kill
}
