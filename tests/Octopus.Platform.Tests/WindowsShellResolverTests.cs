using Octopus.Platform;
using Octopus.Platform.Windows;
using Xunit;

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
}
