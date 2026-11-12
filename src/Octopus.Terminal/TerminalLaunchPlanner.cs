using Octopus.Core.Agents;
using Octopus.Core.Terminals;
using Octopus.Platform;

namespace Octopus.Terminal;

/// <summary>Plans shell and agent launches from the platform services. Pure: it starts nothing.</summary>
public sealed class TerminalLaunchPlanner(
    IShellResolver shells,
    IShellCommandBuilder commandBuilder,
    ITerminalEnvironment environment) : ITerminalLaunchPlanner
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

    private ShellInfo ResolveShell(string? shellPath) =>
        (string.IsNullOrWhiteSpace(shellPath) ? null : shells.Describe(shellPath)) ?? shells.GetDefaultShell();

    private static string ResolveDirectory(string? directory) =>
        !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
            ? directory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
