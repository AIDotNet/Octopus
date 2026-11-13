using Octopus.Core.Agents;
using Octopus.Platform;
using Xunit;

namespace Octopus.Terminal.Tests;

public class TerminalLaunchPlannerTests
{
    private sealed class FakeResolver : IShellResolver
    {
        public static readonly ShellInfo Default = new("/bin/zsh", "zsh", ShellKind.Posix);
        public static readonly ShellInfo Fish = new("/opt/fish", "fish", ShellKind.Fish);
        public ShellInfo GetDefaultShell() => Default;
        public ShellInfo? Describe(string path) => path == Fish.Path ? Fish : null;
        public IReadOnlyList<string> GetInteractiveArguments(ShellInfo shell) => ["-l", $"--for-{shell.Name}"];
        public IReadOnlyList<string> GetCommandArguments(ShellInfo shell, string commandLine) => ["-l", "-i", "-c", commandLine];
    }

    private sealed class FakeBuilder : IShellCommandBuilder
    {
        public string Quote(string argument, ShellKind kind) => $"<{argument}>";
        public string Build(string executable, IEnumerable<string> arguments, ShellKind kind) =>
            $"{kind}:{executable} {string.Join(' ', arguments.Select(a => Quote(a, kind)))}";
        public IReadOnlyList<string> Split(string commandLine) => commandLine.Split(' ');
    }

    private sealed class FakeEnvironment : ITerminalEnvironment
    {
        public IReadOnlyDictionary<string, string> Build(IReadOnlyDictionary<string, string>? extra = null)
        {
            var result = new Dictionary<string, string> { ["TERM"] = "xterm-256color" };
            if (extra is not null) foreach (var (k, v) in extra) result[k] = v;
            return result;
        }
    }

    private static TerminalLaunchPlanner Planner() => new(new FakeResolver(), new FakeBuilder(), new FakeEnvironment());

    [Fact]
    public void PlanShell_uses_default_shell_and_interactive_arguments()
    {
        var spec = Planner().PlanShell(Path.GetTempPath());
        Assert.Equal("/bin/zsh", spec.FileName);
        Assert.Equal(["-l", "--for-zsh"], spec.Arguments);
        Assert.Equal("xterm-256color", spec.Environment["TERM"]);
        Assert.Equal(Path.GetTempPath(), spec.WorkingDirectory);
    }

    [Fact]
    public void PlanShell_uses_chosen_shell_when_it_exists_otherwise_default()
    {
        Assert.Equal("/opt/fish", Planner().PlanShell(null, "/opt/fish").FileName);
        Assert.Equal("/bin/zsh", Planner().PlanShell(null, "/does/not/exist").FileName);
    }

    [Fact]
    public void Missing_directory_falls_back_to_home()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(home, Planner().PlanShell("/no/such/dir/at/all").WorkingDirectory);
        Assert.Equal(home, Planner().PlanShell(null).WorkingDirectory);
    }

    [Fact]
    public void PlanAgent_runs_quoted_command_through_shell_with_agent_environment()
    {
        var command = new AgentCommand("claude", ["--model", "x y"], new Dictionary<string, string> { ["FOO"] = "bar" });
        var spec = Planner().PlanAgent(command, Path.GetTempPath(), "/opt/fish");
        Assert.Equal("/opt/fish", spec.FileName);
        Assert.Equal(["-l", "-i", "-c", "Fish:claude <--model> <x y>"], spec.Arguments);
        Assert.Equal("bar", spec.Environment["FOO"]);
        Assert.Equal("xterm-256color", spec.Environment["TERM"]);
        Assert.Equal(Path.GetTempPath(), spec.WorkingDirectory);
    }

    [Fact]
    public void PlanAgent_falls_back_to_home_for_missing_directory()
    {
        var command = new AgentCommand("codex", [], new Dictionary<string, string>());
        var spec = Planner().PlanAgent(command, "/no/such/dir");
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), spec.WorkingDirectory);
        Assert.Equal("Posix:codex ", spec.Arguments[^1]);
    }

    [Fact]
    public void PlanRemoteShell_runs_ssh_with_the_host_alias_through_the_shell_in_the_local_home()
    {
        var spec = Planner().PlanRemoteShell("prod-web", "/srv/app", "/opt/fish");

        Assert.Equal("ssh", spec.FileName);
        Assert.Equal(["-t", "--", "prod-web", TerminalLaunchPlanner.RemoteShellCommand("/srv/app")], spec.Arguments);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), spec.WorkingDirectory);
    }

    [Fact]
    public void RemoteShellCommand_enters_the_folder_quoted_and_starts_the_login_shell()
    {
        Assert.Equal(
            "cd -- '/srv/my app' || printf '%s\\n' 'Octopus: this folder is not on the host; the login folder is open instead.'; exec $SHELL -l",
            TerminalLaunchPlanner.RemoteShellCommand("/srv/my app"));
    }

    [Theory]
    [InlineData("~", "cd -- ~ || ")]
    [InlineData("~/code/app", "cd -- ~/'code/app' || ")]
    [InlineData("/srv/it's", "cd -- '/srv/it'\\''s' || ")]
    public void RemoteShellCommand_keeps_tilde_for_the_host_and_quotes_quotes(string directory, string start) =>
        Assert.StartsWith(start, TerminalLaunchPlanner.RemoteShellCommand(directory));

    [Fact]
    public void RemoteShellCommand_without_a_folder_only_starts_the_login_shell()
    {
        Assert.Equal("exec $SHELL -l", TerminalLaunchPlanner.RemoteShellCommand(null));
        Assert.Equal("exec $SHELL -l", TerminalLaunchPlanner.RemoteShellCommand("  "));
    }

    [Fact]
    public void PlanRemoteShell_puts_the_shared_connection_options_before_the_host()
    {
        var spec = Planner().PlanRemoteShell("prod-web", null, "/opt/fish", ["-o", "ControlMaster=auto"]);

        Assert.Equal(["-o", "ControlMaster=auto", "-t", "--", "prod-web", TerminalLaunchPlanner.RemoteShellCommand(null)], spec.Arguments);
    }

    [Fact]
    public void PlanMoshShell_runs_mosh_with_the_script_as_one_argument()
    {
        var spec = Planner().PlanMoshShell("盒子", "/srv/app");

        Assert.Equal("mosh", spec.FileName);
        Assert.Equal(["--", "盒子", "sh", "-c", TerminalLaunchPlanner.RemoteShellCommand("/srv/app")], spec.Arguments);
    }

    [Fact]
    public void PlanTelnet_runs_telnet_with_the_host_and_the_port_when_there_is_one()
    {
        Assert.Equal(["10.0.0.1", "2323"], Planner().PlanTelnet("10.0.0.1", 2323).Arguments);
        Assert.Equal(["10.0.0.1"], Planner().PlanTelnet("10.0.0.1", null).Arguments);
        Assert.Equal("telnet", Planner().PlanTelnet("10.0.0.1", null).FileName);
    }
}
