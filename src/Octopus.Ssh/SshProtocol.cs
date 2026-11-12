namespace Octopus.Ssh;

/// <summary>The steps of an ssh connection, in the order they happen (as the debug log of <c>ssh -E</c> with <c>LogLevel=DEBUG1</c> shows them).</summary>
public enum SshStage
{
    /// <summary>The TCP connection to the host is being made.</summary>
    Connecting = 0,
    /// <summary>The two ends agree on the protocol version and the encryption.</summary>
    Negotiating = 1,
    /// <summary>The host's key is checked against the known hosts.</summary>
    CheckingHostKey = 2,
    /// <summary>The person is authenticated (a key, a password, an agent).</summary>
    Authenticating = 3,
    /// <summary>The session is being opened on the host.</summary>
    OpeningSession = 4,
    /// <summary>The session is open: the host's shell is starting.</summary>
    Ready = 5,
}

/// <summary>Reads the lines of ssh's debug log: which step a line reports, and which line says the connection failed.</summary>
public static class SshProtocol
{
    /// <summary>The line without ssh's level prefix (<c>debug1: </c>), as it is shown to the person.</summary>
    public static string Clean(string line)
    {
        var text = line.Trim();
        if (text.StartsWith("debug", StringComparison.Ordinal) && text.Length > 6 && char.IsAsciiDigit(text[5]) && text[6] == ':')
        {
            return text[7..].TrimStart();
        }
        return text;
    }

    /// <summary>The step a line reports, or null for a line that says nothing about the connection's progress.</summary>
    public static SshStage? StageOf(string line)
    {
        var text = Clean(line);
        if (text.Contains("Entering interactive session", StringComparison.Ordinal)
            || text.Contains("mux_client_request_session", StringComparison.Ordinal))
            return SshStage.Ready;
        if (text.Contains("channel 0: new session", StringComparison.Ordinal)
            || text.Contains("Sending environment", StringComparison.Ordinal)
            || text.Contains("Sending command", StringComparison.Ordinal))
            return SshStage.OpeningSession;
        if (text.Contains("Authenticating to", StringComparison.Ordinal)
            || text.Contains("Authentications that can continue", StringComparison.Ordinal)
            || text.Contains("Next authentication method", StringComparison.Ordinal)
            || text.Contains("Offering public key", StringComparison.Ordinal)
            || text.Contains("Trying private key", StringComparison.Ordinal)
            || text.Contains("Authenticated to ", StringComparison.Ordinal))
            return SshStage.Authenticating;
        if (text.Contains("Server host key:", StringComparison.Ordinal)
            || text.Contains("Permanently added", StringComparison.Ordinal)
            || text.Contains("is known and matches", StringComparison.Ordinal))
            return SshStage.CheckingHostKey;
        if (text.Contains("Connection established.", StringComparison.Ordinal)
            || text.Contains("Local version string", StringComparison.Ordinal)
            || text.Contains("Remote protocol version", StringComparison.Ordinal)
            || text.Contains("SSH2_MSG_KEXINIT", StringComparison.Ordinal)
            || text.Contains("kex: algorithm", StringComparison.Ordinal))
            return SshStage.Negotiating;
        if (text.Contains("Connecting to ", StringComparison.Ordinal))
            return SshStage.Connecting;
        return null;
    }

    /// <summary>The text of a line that says the connection failed (wrong password, unknown host, timeout...), or null.</summary>
    public static string? FailureOf(string line)
    {
        var text = Clean(line);
        string[] failures =
        [
            "Permission denied",
            "Could not resolve hostname",
            "Connection refused",
            "Connection timed out",
            "Operation timed out",
            "No route to host",
            "Network is unreachable",
            "Host key verification failed",
            "Too many authentication failures",
            "Timeout, server",
            "Connection closed by",
            "Connection reset by",
            "kex_exchange_identification",
            "ssh_exchange_identification",
            "Bad configuration option",
            "Bad permissions",
            "Could not open",
        ];
        return failures.Any(f => text.Contains(f, StringComparison.Ordinal)) ? text : null;
    }
}
