using Octopus.Core.Agents;
using Octopus.Core.Terminals;
using Octopus.Platform;

namespace Octopus.Terminal;

/// <summary>Plans shell and agent launches from the platform services. Pure: it starts nothing.</summary>
public sealed class TerminalLaunchPlanner(
    IShellResolver shells,
    IShellCommandBuilder commandBuilder,
    ITerminalEnvironment environment,
    IExecutableLocator? locator = null) : ITerminalLaunchPlanner
{
    /// <inheritdoc />
    public LaunchSpec PlanShell(string? workingDirectory, string? shellPath = null)
    {
        var shell = ResolveShell(shellPath);
        return new LaunchSpec(
            shell.Path,
            shells.GetInteractiveArguments(shell),
            ResolveDirectory(workingDirectory),
            environment.Build());
    }

    /// <inheritdoc />
    public LaunchSpec PlanAgent(AgentCommand command, string? workingDirectory, string? shellPath = null)
    {
        var shell = ResolveShell(shellPath);
        var commandLine = commandBuilder.Build(command.Executable, command.Arguments, shell.Kind);
        return new LaunchSpec(
            shell.Path,
            shells.GetCommandArguments(shell, commandLine),
            ResolveDirectory(workingDirectory),
            environment.Build(command.Environment));
    }

    /// <inheritdoc />
    public LaunchSpec PlanRemoteShell(string host, string? remoteDirectory, string? shellPath = null, IReadOnlyList<string>? sshOptions = null)
    {
        // ssh is started directly, with its arguments as they are: no shell in between, so no quoting for the shell's dialect.
        IReadOnlyList<string> options = sshOptions ?? [];
        var built = environment.Build();
        var ssh = FindSsh(built);
        return new LaunchSpec(
            ssh,
            [.. options, "-t", "--", host, RemoteShellCommand(remoteDirectory)],
            ResolveDirectory(null),
            built);
    }

    /// <inheritdoc />
    public LaunchSpec PlanMoshShell(string host, string? remoteDirectory)
    {
        // mosh quotes each word of the command for the host, so the script goes as one argument to sh.
        var built = environment.Build();
        return new LaunchSpec(
            FindProgram("mosh", built),
            ["--", host, "sh", "-c", RemoteShellCommand(remoteDirectory)],
            ResolveDirectory(null),
            built);
    }

    /// <inheritdoc />
    public LaunchSpec PlanTelnet(string host, int? port)
    {
        var built = environment.Build();
        return new LaunchSpec(
            FindProgram("telnet", built),
            port is { } number ? [host, number.ToString(System.Globalization.CultureInfo.InvariantCulture)] : [host],
            ResolveDirectory(null),
            built);
    }

    /// <summary>The ssh program: the one on the terminal's PATH when it can be found, otherwise "ssh" for the system to find.</summary>
    private string FindSsh(IReadOnlyDictionary<string, string> built) => FindProgram("ssh", built);

    private string FindProgram(string name, IReadOnlyDictionary<string, string> built)
    {
        var path = built.TryGetValue("PATH", out var value) ? value : null;
        return locator?.FindAll(name, path).FirstOrDefault() ?? name;
    }

    /// <summary>
    /// What ssh runs on the host: enter the folder, or say that it is missing and stay in the login folder, then start the
    /// login shell. Written for POSIX-style shells, which is what hosts run in practice.
    /// </summary>
    internal static string RemoteShellCommand(string? directory)
    {
        var enter = string.IsNullOrWhiteSpace(directory)
            ? ""
            : "cd -- " + RemotePath(directory.Trim()) + " || printf '%s\\n' 'Octopus: this folder is not on the host; the login folder is open instead.'; ";
        return enter + "exec $SHELL -l";
    }

    /// <summary>A path on the host: <c>~</c> stays for the host's shell to expand, the rest is quoted so nothing in it is read as shell syntax.</summary>
    private static string RemotePath(string path) =>
        path == "~" ? "~"
        : path.StartsWith("~/", StringComparison.Ordinal) ? "~/" + PosixQuote(path[2..])
        : PosixQuote(path);

    private static string PosixQuote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private ShellInfo ResolveShell(string? shellPath) =>
        (string.IsNullOrWhiteSpace(shellPath) ? null : shells.Describe(shellPath)) ?? shells.GetDefaultShell();

    private static string ResolveDirectory(string? directory) =>
        !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
            ? directory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
