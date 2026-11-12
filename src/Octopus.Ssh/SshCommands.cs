namespace Octopus.Ssh;

/// <summary>
/// The ssh options and the quoting for the runs the app makes on its own (listing folders, copying files), as opposed to
/// the interactive terminal. Every run is <c>ssh [options] -- host command</c>; the command is one string that the host's
/// shell runs, so every path in it is quoted here.
/// </summary>
public static class SshCommands
{
    /// <summary>
    /// Options that let the runs for one host share one connection (OpenSSH's ControlMaster): the first one connects, the
    /// others reuse the connection for a while after the last. Empty on Windows, where OpenSSH has no such sockets.
    /// </summary>
    /// <param name="controlDirectory">Where the sockets are kept. Keep it short: a socket path is limited to about 100 characters.</param>
    public static IReadOnlyList<string> SharedConnection(string controlDirectory) =>
        OperatingSystem.IsWindows()
            ? []
            : ["-o", "ControlMaster=auto", "-o", "ControlPath=" + Path.Combine(controlDirectory, "%C"), "-o", "ControlPersist=300"];

    /// <summary>
    /// Options for a run that may only ride on the connection that <see cref="SharedConnection"/> made (the terminal's):
    /// it never becomes a master itself. Empty where there are no such connections (Windows). Note that without a master
    /// to join ssh connects by itself, so what is run with these has to be ready for that (a <c>-O</c> command is not: it
    /// fails with "Control socket connect").
    /// </summary>
    public static IReadOnlyList<string> SharedClient(string controlDirectory) =>
        OperatingSystem.IsWindows()
            ? []
            : ["-o", "ControlMaster=no", "-o", "ControlPath=" + Path.Combine(controlDirectory, "%C")];

    /// <summary>
    /// Options that make ssh notice a connection that has gone silent: without them a network that drops without a word
    /// keeps ssh (and the terminal running it) waiting for good. With them ssh ends after about thirty seconds.
    /// </summary>
    public static IReadOnlyList<string> KeepAlive => ["-o", "ServerAliveInterval=10", "-o", "ServerAliveCountMax=3"];

    /// <summary>Options for a run where nobody can answer a prompt: a request for a password or passphrase fails at once.</summary>
    public static IReadOnlyList<string> Unattended => ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10", .. KeepAlive];

    /// <summary>The argument list of one run.</summary>
    public static IReadOnlyList<string> Arguments(string host, string remoteCommand, IEnumerable<string> options) =>
        [.. options, "--", host, remoteCommand];

    /// <summary>A path on the host as a shell word: <c>~</c> and <c>~/</c> stay for the host's shell to expand, the rest is quoted.</summary>
    public static string RemotePath(string path) =>
        path == "~" ? "~"
        : path.StartsWith("~/", StringComparison.Ordinal) ? "~/" + Quote(path[2..])
        : Quote(path);

    /// <summary>A text as one POSIX shell word, in single quotes.</summary>
    public static string Quote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
