using Octopus.Core.Projects;

namespace Octopus.Ssh;

/// <summary>
/// The ssh runs of a tunnel that rides on the connection the terminal made (design/11-SSH隧道.md §4, "shared connection").
/// The forwards are then the master connection's, not a process's: they are added with <c>-O forward</c> and taken away with
/// <c>-O cancel</c>, and the master has to be kept from ending while they are needed, which an <see cref="Anchor"/> does.
/// Every run is given the options of <see cref="SshCommands.SharedClient"/> first.
/// </summary>
public static class SshSharedCommand
{
    /// <summary>Asks the master of the host whether it is there: exit code 0 and "Master running", or "Control socket connect" and 255.</summary>
    public static IReadOnlyList<string> Check(string host, IReadOnlyList<string> client) =>
        [.. client, "-O", "check", "--", host];

    /// <summary>
    /// Makes the master listen for the tunnel. Asked twice for the same forward it answers twice with success and keeps
    /// one. It fails with 255 and a line that <see cref="SshTunnelLog.EventOf"/> reads (a port it cannot bind, a port
    /// the host does not give).
    /// </summary>
    public static IReadOnlyList<string> Forward(string host, TunnelSpec spec, IReadOnlyList<string> client) =>
        [.. client, "-O", "forward", SshTunnelCommand.Flag(spec), SshTunnelCommand.Forward(spec), "--", host];

    /// <summary>
    /// Takes the forward away, given the same spec it was added with. A forward that is not there is no error for the
    /// caller, but <b>the exit code is 0 either way</b>: "port not forwarded" is on the error output.
    /// </summary>
    public static IReadOnlyList<string> Cancel(string host, TunnelSpec spec, IReadOnlyList<string> client) =>
        [.. client, "-O", "cancel", SshTunnelCommand.Flag(spec), SshTunnelCommand.Forward(spec), "--", host];

    /// <summary>
    /// A session on the master that does nothing but wait: <c>cat</c> on the host, with its input held open by the
    /// app. It is what keeps the master from ending when the terminal's session has (the master's <c>ControlPersist</c>
    /// does not count a forward as something in use: checked, the master and its forwards were gone seconds after the
    /// last session). It ends when its input is closed or the process is killed, and the <c>cat</c> goes with it; and it
    /// ends by itself when the master does, which is how the app finds out that the connection is lost.
    /// <c>BatchMode</c> and the timeout are for the case that there is no master after all: ssh then makes a
    /// connection of its own, which must not ask for anything.
    /// </summary>
    public static IReadOnlyList<string> Anchor(string host, IReadOnlyList<string> client) =>
        [.. client, "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-T", "--", host, "cat"];
}
