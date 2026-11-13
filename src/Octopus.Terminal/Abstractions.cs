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

    /// <summary>
    /// An interactive shell on an SSH host, started in <paramref name="remoteDirectory"/> on that host (null: its login
    /// folder). The local ssh program runs through the user's shell, as an agent does, and reads ~/.ssh/config for the
    /// connection. Nothing here is checked on the host: a missing folder is reported in the terminal.
    /// </summary>
    LaunchSpec PlanRemoteShell(string host, string? remoteDirectory, string? shellPath = null, IReadOnlyList<string>? sshOptions = null);

    /// <summary>
    /// Like <see cref="PlanRemoteShell"/> with the mosh program, which starts through ssh (and so reads ~/.ssh/config too)
    /// and then keeps the session alive over a network that changes or sleeps. Needs <c>mosh</c> here and <c>mosh-server</c> on the host.
    /// </summary>
    LaunchSpec PlanMoshShell(string host, string? remoteDirectory);

    /// <summary>The telnet program connected to <paramref name="host"/> and <paramref name="port"/> (null: telnet's own, 23). There is no folder: telnet has no way to start in one.</summary>
    LaunchSpec PlanTelnet(string host, int? port);
}
