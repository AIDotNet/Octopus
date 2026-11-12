using Octopus.Platform.MacOS;
using Octopus.Platform.Unix;

namespace Octopus.Platform.Tests;

public class MacIntegrationTests
{
    [Fact]
    public void Default_shell_exists()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var shell = new MacShellResolver(new ProcessRunner()).GetDefaultShell();
        Assert.True(File.Exists(shell.Path), shell.Path);
        Assert.NotNull(new MacShellResolver(new ProcessRunner()).Describe(shell.Path));
    }

    [Fact]
    public async Task Login_environment_has_path()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var runner = new ProcessRunner();
        var env = await new UnixLoginEnvironment(new MacShellResolver(runner), runner).GetAsync();
        Assert.False(string.IsNullOrEmpty(env["PATH"]));
    }

    [Fact]
    public void Terminal_environment_has_utf8_lang()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var env = new MacTerminalEnvironment(new ProcessRunner(), new Dictionary<string, string>()).Build();
        Assert.Contains("UTF-8", env["LANG"]);
    }
}
