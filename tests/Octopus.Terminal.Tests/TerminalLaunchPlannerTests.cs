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
}
