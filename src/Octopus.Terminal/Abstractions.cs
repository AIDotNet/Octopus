using Octopus.Core.Agents;
using Octopus.Core.Terminals;

namespace Octopus.Terminal;

/// <summary>
/// One pseudo-terminal with a process attached. The shape leaves room for a later implementation that talks to a
/// separate pty host process, so nothing above this interface knows which pty library is underneath.
/// </summary>
public interface ITerminalConnection : IAsyncDisposable
{
    int ProcessId { get; }

    /// <summary>The process's output, in order, until it ends. Read by one consumer.</summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken cancellationToken = default);

    ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default);

    ValueTask ResizeAsync(int columns, int rows, int pixelWidth, int pixelHeight);

    ValueTask SignalAsync(TerminalSignal signal);

    /// <summary>Completes after the process has exited and its output has been read to the end.</summary>
    Task<TerminalExit> Exited { get; }

    /// <summary>The name of the foreground process, to tell "a program is running" from "the shell is idle". Null when unknown.</summary>
    ValueTask<string?> GetForegroundProcessNameAsync();
}

public interface ITerminalConnectionFactory
{
    /// <summary>Starts <paramref name="spec"/> in a new pty of the given size. Throws when the process cannot start.</summary>
    ITerminalConnection Start(LaunchSpec spec, int columns, int rows);
}

/// <summary>Turns "what the user wants to open" into exactly what the pty starts. See design/06-终端设计.md §3.</summary>
public interface ITerminalLaunchPlanner
{
    /// <summary>The user's shell (or <paramref name="shellPath"/>), interactive, in <paramref name="workingDirectory"/>.</summary>
    LaunchSpec PlanShell(string? workingDirectory, string? shellPath = null);

    /// <summary>
    /// An agent command, run through the user's login + interactive shell so that PATH, aliases and functions are
    /// those of the user's own terminal. Arguments are quoted for that shell; nothing in them is reinterpreted.
    /// </summary>
    LaunchSpec PlanAgent(AgentCommand command, string? workingDirectory, string? shellPath = null);
}
