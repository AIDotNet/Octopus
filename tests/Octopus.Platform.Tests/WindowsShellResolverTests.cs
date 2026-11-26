using Octopus.Platform.Windows;

namespace Octopus.Platform.Tests;

public class WindowsShellResolverTests
{
    private sealed class FakeLocator(Dictionary<string, string[]> found) : IExecutableLocator
    {
        public IReadOnlyList<string> FindAll(string name, string? pathVariable) =>
            found.TryGetValue(name, out var paths) ? paths : [];
    }

    [Fact]
    public void A_pwsh_on_the_path_is_the_default_shell_and_the_search_does_not_throw()
    {
        // Files that are no programs have no file version: the comparison between candidates must cope with that.
        var directory = Directory.CreateTempSubdirectory("octopus-shells");
        try
        {
            var first = Path.Combine(directory.FullName, "pwsh.exe");
            var second = Path.Combine(directory.FullName, "other", "pwsh.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(second)!);
            File.WriteAllText(first, "");
            File.WriteAllText(second, "");
            var resolver = new WindowsShellResolver(new FakeLocator(new() { ["pwsh.exe"] = [first, second] }));

            var shell = resolver.GetDefaultShell();

            Assert.Equal(ShellKind.PowerShell, shell.Kind);
            Assert.Equal("pwsh", shell.Name);
            // A PowerShell installed on the machine may be newer than the stand-ins, so which one wins is not asserted.
            Assert.True(File.Exists(shell.Path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Without_any_pwsh_a_shell_is_still_found()
    {
        var shell = new WindowsShellResolver(new FakeLocator([])).GetDefaultShell();
        Assert.False(string.IsNullOrEmpty(shell.Path));
    }

    [Fact]
    public void Interactive_arguments_pass_working_directory_for_pwsh_only()
    {
        var resolver = new WindowsShellResolver(new FakeLocator([]));
        var pwsh = new ShellInfo(@"C:\Program Files\PowerShell\7\pwsh.exe", "pwsh", ShellKind.PowerShell);
        var powershell5 = new ShellInfo(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "powershell", ShellKind.PowerShell);
        var cmd = new ShellInfo(@"C:\Windows\System32\cmd.exe", "cmd", ShellKind.Cmd);
        var gitBash = new ShellInfo(@"C:\Program Files\Git\bin\bash.exe", "bash", ShellKind.Posix);

        Assert.Equal(["-NoLogo", "-WorkingDirectory", @"D:\code\app"], resolver.GetInteractiveArguments(pwsh, @"D:\code\app"));
        Assert.Equal(["-NoLogo"], resolver.GetInteractiveArguments(pwsh, null));
        Assert.Equal(["-NoLogo"], resolver.GetInteractiveArguments(powershell5, @"D:\code\app"));
        Assert.Empty(resolver.GetInteractiveArguments(cmd, @"D:\code\app"));
        Assert.Equal(["-l"], resolver.GetInteractiveArguments(gitBash, @"D:\code\app"));
    }

    [Fact]
    public void WindowsTerminalEnvironment_sets_chere_invoking_and_strips_stale_pwd()
    {
        var env = new WindowsTerminalEnvironment(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = @"C:\Windows\System32",
                ["PWD"] = @"C:\Users\me",
                ["OLDPWD"] = @"C:\",
            },
            "1.0").Build();

        Assert.Equal("enabled_from_arguments", env["CHERE_INVOKING"]);
        Assert.False(env.ContainsKey("PWD"));
        Assert.False(env.ContainsKey("OLDPWD"));
    }
}
