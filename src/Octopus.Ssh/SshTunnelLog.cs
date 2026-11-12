namespace Octopus.Ssh;

/// <summary>What a line of a tunnel's ssh log says about the tunnel.</summary>
public enum SshTunnelEvent
{
    /// <summary>Nothing about the tunnel's progress.</summary>
    None,
    /// <summary>The host accepted the login.</summary>
    Authenticated,
    /// <summary>The host confirmed the forward asked for (remote forwards say so).</summary>
    ForwardConfirmed,
    /// <summary>ssh finished setting up and runs the forward.</summary>
    SessionOpen,
    /// <summary>The port on this computer is taken, or cannot be bound.</summary>
    LocalPortUnavailable,
    /// <summary>The host did not give the port: it is taken, or the server does not allow forwarding.</summary>
    RemoteForwardRefused,
    /// <summary>The host refused the login: the tunnel needs a key or an agent, it cannot ask for a password.</summary>
    LoginDenied,
    /// <summary>The host's key is not the one that is known.</summary>
    HostKeyRejected,
    /// <summary>The connection could not be made or was lost: the network, not the setup. Trying again may work.</summary>
    ConnectionLost,
    /// <summary>One forwarded connection could not reach its target; the tunnel itself stays up.</summary>
    TargetUnreachable,
    /// <summary>The server does not allow forwarding (<c>AllowTcpForwarding no</c>).</summary>
    ForwardingProhibited,
    /// <summary>There is no connection to ride on: the control socket of the shared connection is not there (or nobody answers).</summary>
    NoSharedConnection
}

/// <summary>
/// Reads the lines of the debug log of a tunnel's ssh run (<c>LogLevel=DEBUG1</c>). The wording is OpenSSH's; it was
/// checked against the logs of real runs (OpenSSH 10.5 to an OpenSSH server): a local and a remote forward, a port that
/// is taken on either side, a refused login, an unreachable or unknown host, a connection ended by the host and one that
/// went silent. The samples are in the tests.
/// </summary>
public static class SshTunnelLog
{
    /// <summary>The event a line reports, or <see cref="SshTunnelEvent.None"/>.</summary>
    public static SshTunnelEvent EventOf(string line)
    {
        var text = SshProtocol.Clean(line);

        // A single forwarded connection failing is not the tunnel failing: these lines come after the tunnel is up.
        if (text.Contains("open failed: administratively prohibited", StringComparison.Ordinal))
            return SshTunnelEvent.ForwardingProhibited;
        // "open failed: connect failed" is a local forward whose target (on the host) refused; "connection failed" is a
        // remote forward whose target (here) refused.
        if (text.Contains("open failed: connect failed", StringComparison.Ordinal)
            || text.Contains("connection failed:", StringComparison.Ordinal)
            || (text.Contains("connect_to ", StringComparison.Ordinal) && text.Contains("failed", StringComparison.Ordinal)))
            return SshTunnelEvent.TargetUnreachable;

        // `ssh -O ...` with no master: "Control socket connect(<path>): No such file or directory" (or "Connection refused" for a stale socket).
        if (text.Contains("Control socket connect", StringComparison.Ordinal))
            return SshTunnelEvent.NoSharedConnection;

        if (text.Contains("Address already in use", StringComparison.Ordinal)
            || text.Contains("cannot listen to port", StringComparison.Ordinal)
            // What the master says when it cannot bind a forward it was asked for (`ssh -O forward -L`).
            || text.Contains("Port forwarding failed", StringComparison.Ordinal)
            || text.Contains("Could not request local forwarding", StringComparison.Ordinal)
            || (text.Contains("bind", StringComparison.Ordinal) && text.Contains("Permission denied", StringComparison.Ordinal)))
            return SshTunnelEvent.LocalPortUnavailable;
        if (text.Contains("remote port forwarding failed", StringComparison.Ordinal)
            || text.Contains("Could not request remote forwarding", StringComparison.Ordinal)
            || text.Contains("remote forward failure", StringComparison.Ordinal))
            return SshTunnelEvent.RemoteForwardRefused;
        if (text.Contains("Host key verification failed", StringComparison.Ordinal)
            || text.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.Ordinal))
            return SshTunnelEvent.HostKeyRejected;
        if (text.Contains("Permission denied", StringComparison.Ordinal)
            || text.Contains("Too many authentication failures", StringComparison.Ordinal))
            return SshTunnelEvent.LoginDenied;
        if (IsConnectionLoss(text))
            return SshTunnelEvent.ConnectionLost;

        if (text.Contains("remote forward success", StringComparison.Ordinal))
            return SshTunnelEvent.ForwardConfirmed;
        if (text.Contains("Entering interactive session", StringComparison.Ordinal))
            return SshTunnelEvent.SessionOpen;
        if (text.StartsWith("Authenticated to ", StringComparison.Ordinal))
            return SshTunnelEvent.Authenticated;
        return SshTunnelEvent.None;
    }

    /// <summary>True for an event that ends the run and is no use retrying: the setup has to change first.</summary>
    public static bool NeedsChange(SshTunnelEvent value) =>
        value is SshTunnelEvent.LocalPortUnavailable or SshTunnelEvent.RemoteForwardRefused
            or SshTunnelEvent.LoginDenied or SshTunnelEvent.HostKeyRejected;

    private static bool IsConnectionLoss(string text) =>
        text.Contains("Connection timed out", StringComparison.Ordinal)
        || text.Contains("Operation timed out", StringComparison.Ordinal)
        || text.Contains("Connection reset", StringComparison.Ordinal)
        || text.Contains("Connection closed by", StringComparison.Ordinal)
        // What ssh says when the host ends a connection that was up: "Connection to 10.0.0.5 closed by remote host."
        || text.Contains("closed by remote host", StringComparison.Ordinal)
        || text.Contains("Connection refused", StringComparison.Ordinal)
        || text.Contains("Timeout, server", StringComparison.Ordinal)
        || text.Contains("No route to host", StringComparison.Ordinal)
        || text.Contains("Network is unreachable", StringComparison.Ordinal)
        || text.Contains("Could not resolve hostname", StringComparison.Ordinal)
        || text.Contains("Broken pipe", StringComparison.Ordinal)
        || text.Contains("kex_exchange_identification", StringComparison.Ordinal)
        || text.Contains("ssh_exchange_identification", StringComparison.Ordinal);
}
