namespace Octopus.Core.Terminals;

/// <summary>Exactly what a pty starts.</summary>
/// <param name="Environment">The complete environment of the child process.</param>
public sealed record LaunchSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>A planned terminal launch bound to an optional per-process lease released when that process exits, fails, restarts, or closes.</summary>
public sealed record TerminalLaunch(LaunchSpec Spec, IDisposable? Lease = null) : IDisposable
{
    public void Dispose() => Lease?.Dispose();
}

/// <param name="ExitCode">Null when the process was ended by a signal.</param>
public readonly record struct TerminalExit(int? ExitCode, int? Signal);

public enum TerminalSignal
{
    Interrupt,
    Hangup,
    Terminate,
    Kill
}
