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

    /// <summary>
    /// Completes as soon as the underlying process exits, independently of whether trailing PTY output has finished
    /// draining. Defaults to <see cref="Exited"/> for connections that do not distinguish the two.
    /// </summary>
    Task ProcessExited => Exited;

    /// <summary>Completes after the process has exited and its output has been read to the end.</summary>
    Task<TerminalExit> Exited { get; }

    /// <summary>The name of the foreground process, to tell "a program is running" from "the shell is idle". Null when unknown.</summary>
    ValueTask<string?> GetForegroundProcessNameAsync();
}

/// <summary>What a mirror applies to its terminal besides the output bytes.</summary>
public enum TerminalMirrorEventKind
{
    /// <summary>The terminal's grid on the owning side is now <see cref="TerminalMirrorEvent.Columns"/> by <see cref="TerminalMirrorEvent.Rows"/>.</summary>
    Resize,
    /// <summary>The terminal was cleared for a restart of its program.</summary>
    Reset,
    /// <summary>The whole terminal, as the terminal library's own snapshot: it replaces what the mirror has.</summary>
    Snapshot,
}

public readonly record struct TerminalMirrorEvent(TerminalMirrorEventKind Kind, int Columns = 0, int Rows = 0, byte[]? Snapshot = null);

/// <summary>
/// The connection of a terminal whose process another terminal owns (a session on another computer). Besides output it
/// has events that take effect at an exact place in that output: for each event the reader gets one empty chunk from
/// <see cref="ITerminalConnection.ReadOutputAsync"/>, at that place, and takes the event with <see cref="TakeEvent"/>
/// before it goes on reading. <see cref="ITerminalConnection.ResizeAsync"/> only asks: the size changes when a
/// <see cref="TerminalMirrorEventKind.Resize"/> event says so.
/// </summary>
public interface ITerminalMirrorConnection : ITerminalConnection
{
    /// <summary>The event the empty chunk just read stands for; events come out in the order of their chunks.</summary>
    TerminalMirrorEvent? TakeEvent();
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
