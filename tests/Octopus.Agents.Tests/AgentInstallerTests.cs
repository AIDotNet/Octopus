using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Agents.Tests;

public class AgentInstallerTests
{
    private static AgentDefinition Agent(params (string Kind, string Command)[] hints) => new()
    {
        Id = "demo",
        Name = "Demo",
        Detect = new AgentDetect { InstallHints = [.. hints.Select(h => new AgentInstallHint { Kind = h.Kind, Command = h.Command })] },
    };

    private static AgentInstaller Installer(
        Dictionary<string, string[]> tools,
        bool isWindows,
        FakeRunner? runner = null,
        Func<string, CancellationToken, Task<bool>>? isWritable = null) =>
        new(runner ?? new FakeRunner((_, _, _) => new ProcessResult(0, "", "", false)),
            new FakeLocator(tools), new FakeLoginEnvironment("/usr/bin"), () => ["/home/x/.bun/bin"], isWindows,
            isWritable ?? ((_, _) => Task.FromResult(true)));

    /// <summary>A runner where <c>npm prefix -g</c> answers <paramref name="prefix"/> and everything else succeeds.</summary>
    private static FakeRunner NpmPrefix(string prefix) => new((_, args, _) =>
        args.SequenceEqual(["prefix", "-g"]) ? new ProcessResult(0, prefix + "\n", "", false) : new ProcessResult(0, "", "", false));

    [Fact]
    public async Task Picks_the_first_hint_whose_tool_exists_and_runs_it_through_bash()
    {
        var installer = Installer(new() { ["uv"] = ["/usr/bin/uv"], ["bash"] = ["/bin/bash"] }, isWindows: false);
        var plan = await installer.PlanAsync(Agent(("npm", "npm i -g demo"), ("uv", "uv tool install demo")));

        Assert.NotNull(plan);
        Assert.Equal("uv", plan.Tool);
        Assert.Equal("/bin/bash", plan.Shell);
        Assert.Equal(["-c", "uv tool install demo"], plan.ShellArguments);
        Assert.Equal(InstallElevation.None, plan.Elevation);
        Assert.False(plan.IsScript);
    }

    [Fact]
    public async Task Download_pages_and_missing_tools_give_no_plan()
    {
        var installer = Installer(new() { ["bash"] = ["/bin/bash"] }, isWindows: false);
        Assert.Null(await installer.PlanAsync(Agent(("native", "https://example.com/releases"), ("npm", "npm i -g demo"))));
        Assert.Null(await installer.PlanAsync(Agent()));
    }

    [Fact]
    public async Task Windows_skips_unix_pipe_scripts_and_uses_powershell_for_irm()
    {
        var installer = Installer(new()
        {
            ["curl"] = ["C:\\Windows\\System32\\curl.exe"],
            ["powershell"] = ["C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"],
            ["cmd"] = ["C:\\Windows\\System32\\cmd.exe"],
            ["npm"] = ["C:\\npm\\npm.cmd"],
        }, isWindows: true);

        var plan = await installer.PlanAsync(Agent(("script", "curl -fsSL https://x/install.sh | bash"), ("npm", "npm i -g demo")));
        Assert.NotNull(plan);
        Assert.Equal("npm", plan.Tool);
        Assert.EndsWith("cmd.exe", plan.Shell);

        // irm is a PowerShell alias, not a file: the hint is still runnable when PowerShell itself exists.
        var ps = await installer.PlanAsync(Agent(("script", "irm https://x/install.ps1 | iex")));
        Assert.NotNull(ps);
        Assert.Equal("powershell", ps.Tool);
        Assert.EndsWith("powershell.exe", ps.Shell);
        Assert.Equal("irm https://x/install.ps1 | iex", ps.ShellArguments[^1]);
        Assert.True(ps.IsScript);
        Assert.Equal(InstallElevation.Possible, ps.Elevation);
    }

    [Fact]
    public void Unix_refuses_powershell_hints()
    {
        var installer = Installer([], isWindows: false);
        Assert.Null(installer.ShellFor(new AgentInstallHint { Kind = "script", Command = "irm https://x/install.ps1 | iex" }));
        Assert.Equal("bash", installer.ShellFor(new AgentInstallHint { Kind = "script", Command = "curl -fsSL https://x | bash" })!.Value.Program);
    }

    [Fact]
    public async Task Npm_into_a_folder_the_user_cannot_write_needs_admin()
    {
        var checkedDirs = new List<string>();
        var root = Directory.CreateTempSubdirectory("octopus-npm-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "lib", "node_modules"));
            var installer = Installer(
                new() { ["npm"] = ["/usr/local/bin/npm"], ["bash"] = ["/bin/bash"] },
                isWindows: false,
                NpmPrefix(root),
                (dir, _) =>
                {
                    checkedDirs.Add(dir);
                    return Task.FromResult(false);
                });

            var plan = await installer.PlanAsync(Agent(("npm", "npm i -g demo")));
            Assert.Equal(InstallElevation.Required, plan!.Elevation);
            Assert.Equal([Path.Combine(root, "lib", "node_modules")], checkedDirs);

            // Asked once per npm, not per agent.
            await installer.PlanAsync(Agent(("npm", "npm i -g other")));
            Assert.Single(checkedDirs);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task A_writable_npm_prefix_scripts_and_sudo_are_classified()
    {
        var installer = Installer(
            new() { ["npm"] = ["/home/x/.nvm/bin/npm"], ["curl"] = ["/usr/bin/curl"], ["apt"] = ["/usr/bin/apt"], ["bash"] = ["/bin/bash"] },
            isWindows: false,
            NpmPrefix(Path.GetTempPath()));

        Assert.Equal(InstallElevation.None, (await installer.PlanAsync(Agent(("npm", "npm i -g demo"))))!.Elevation);

        var script = await installer.PlanAsync(Agent(("script", "curl -fsSL https://x/install.sh | bash")));
        Assert.True(script!.IsScript);
        Assert.Equal(InstallElevation.Possible, script.Elevation);

        var sudo = await installer.PlanAsync(Agent(("apt", "sudo apt install demo")));
        Assert.Equal("apt", sudo!.Tool);
        Assert.Equal(InstallElevation.Required, sudo.Elevation);
    }

    [Fact]
    public async Task The_elevated_terminal_command_adds_sudo_with_the_full_tool_path_except_for_scripts_and_brew()
    {
        var installer = Installer(
            new() { ["npm"] = ["/usr/local/bin/npm"], ["curl"] = ["/usr/bin/curl"], ["brew"] = ["/opt/homebrew/bin/brew"], ["bash"] = ["/bin/bash"] },
            isWindows: false);

        var npm = (await installer.PlanAsync(Agent(("npm", "npm i -g demo"))))!;
        Assert.Equal(["-c", "sudo /usr/local/bin/npm i -g demo"], installer.BuildTerminalCommand(npm, elevated: true).Arguments);
        Assert.Equal(["-c", "npm i -g demo"], installer.BuildTerminalCommand(npm, elevated: false).Arguments);
        Assert.Equal("/bin/bash", installer.BuildTerminalCommand(npm, elevated: true).Executable);

        var script = (await installer.PlanAsync(Agent(("script", "curl -fsSL https://x | bash"))))!;
        Assert.Equal("curl -fsSL https://x | bash", installer.BuildTerminalCommand(script, elevated: true).Arguments[^1]);

        var brew = (await installer.PlanAsync(Agent(("brew", "brew install demo"))))!;
        Assert.Equal("brew install demo", installer.BuildTerminalCommand(brew, elevated: true).Arguments[^1]);
    }

    [Fact]
    public async Task Install_runs_with_the_extended_path_and_flags_permission_failures()
    {
        var runner = new FakeRunner((_, args, _) => args.SequenceEqual(["prefix", "-g"])
            ? new ProcessResult(1, "", "", false)
            : new ProcessResult(243, "fetching", "npm ERR! code EACCES\nnpm ERR! Error: EACCES: permission denied, mkdir '/usr/local/lib/node_modules/demo'", false));
        var installer = Installer(new() { ["npm"] = ["/usr/bin/npm"], ["bash"] = ["/bin/bash"] }, isWindows: false, runner);
        var plan = await installer.PlanAsync(Agent(("npm", "npm i -g demo")));

        var result = await installer.InstallAsync(plan!);

        Assert.False(result.Succeeded);
        Assert.True(result.NeedsAdmin);
        Assert.StartsWith("fetching\nnpm ERR! code EACCES", result.Output);
        var (_, env) = Assert.Single(runner.Calls, c => c.File == "/bin/bash");
        Assert.Equal("/usr/bin" + Path.PathSeparator + "/home/x/.bun/bin", env!["PATH"]);
    }

    [Fact]
    public async Task An_ordinary_failure_does_not_ask_for_admin()
    {
        var runner = new FakeRunner((_, _, _) => new ProcessResult(1, "", "npm ERR! 404 Not Found - GET https://registry.npmjs.org/demo", false));
        var installer = Installer(new() { ["npm"] = ["/usr/bin/npm"], ["bash"] = ["/bin/bash"] }, isWindows: false, runner);
        var result = await installer.InstallAsync((await installer.PlanAsync(Agent(("npm", "npm i -g demo"))))!);
        Assert.False(result.NeedsAdmin);
    }

    [Fact]
    public async Task Windows_elevates_through_start_process_runas()
    {
        var calls = new List<(string File, IReadOnlyList<string> Args)>();
        var runner = new FakeRunner((file, args, _) =>
        {
            calls.Add((file, args.ToList()));
            return new ProcessResult(0, "", "", false);
        });
        var installer = Installer(new()
        {
            ["npm"] = ["C:\\Program Files\\nodejs\\npm.cmd"],
            ["cmd"] = ["C:\\Windows\\System32\\cmd.exe"],
            ["powershell"] = ["C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"],
        }, isWindows: true, runner);
        Assert.True(installer.CanElevateInBackground);
        var plan = (await installer.PlanAsync(Agent(("npm", "npm i -g @scope/demo"))))!;

        var result = await installer.InstallElevatedAsync(plan);

        Assert.True(result.Succeeded);
        var (file, args) = calls.Last();
        Assert.EndsWith("powershell.exe", file);
        var script = args[^1];
        Assert.Contains("Start-Process -FilePath 'C:\\Windows\\System32\\cmd.exe'", script);
        Assert.Contains("-ArgumentList '/d /s /c \"npm i -g @scope/demo\"'", script);
        Assert.Contains("-Verb RunAs -Wait -PassThru", script);
        Assert.EndsWith("exit $p.ExitCode", script);
    }

    [Theory]
    [InlineData("npm ERR! code EACCES", true)]
    [InlineData("sudo: a terminal is required to read the password", true)]
    [InlineData("error: Permission denied (os error 13)", true)]
    [InlineData("Access is denied.", true)]
    [InlineData("npm ERR! 404 Not Found", false)]
    [InlineData("", false)]
    public void Permission_errors_are_recognized(string output, bool expected) =>
        Assert.Equal(expected, AgentInstaller.LooksLikePermissionError(output));
}
