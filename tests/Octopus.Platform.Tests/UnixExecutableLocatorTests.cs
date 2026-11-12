using Octopus.Platform.Unix;

namespace Octopus.Platform.Tests;

public sealed class UnixExecutableLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octopus-loc-" + Guid.NewGuid().ToString("N"));
    private readonly UnixExecutableLocator _locator = new();

    public UnixExecutableLocatorTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static string File_(string dir, string name, UnixFileMode mode)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "#!/bin/sh\n");
        File.SetUnixFileMode(path, mode);
        return path;
    }

    private const UnixFileMode Exec = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode NoExec = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [Fact]
    public void Returns_all_executables_in_path_order()
    {
        if (OperatingSystem.IsWindows()) return;
        var a = File_(Dir("a"), "tool", Exec);
        var b = File_(Dir("b"), "tool", Exec);
        var result = _locator.FindAll("tool", $"{Dir("b")}:{Dir("a")}");
        Assert.Equal([b, a], result);
    }

    [Fact]
    public void Skips_non_executable_directories_and_broken_links()
    {
        if (OperatingSystem.IsWindows()) return;
        var a = Dir("a");
        File_(a, "tool", NoExec);
        var b = Dir("b");
        File.CreateSymbolicLink(Path.Combine(b, "tool"), Path.Combine(_root, "missing"));
        var c = Dir("c");
        Directory.CreateDirectory(Path.Combine(c, "tool"));
        var d = File_(Dir("d"), "tool", Exec);
        Assert.Equal([d], _locator.FindAll("tool", string.Join(':', a, b, c, Dir("d"))));
    }

    [Fact]
    public void Accepts_link_to_executable_and_dedupes_by_real_path()
    {
        if (OperatingSystem.IsWindows()) return;
        var real = File_(Dir("real"), "tool", Exec);
        var linkDir = Dir("links");
        var link = Path.Combine(linkDir, "tool");
        File.CreateSymbolicLink(link, real);
        // The same directory twice, and a link to the first hit: only the first is kept.
        var result = _locator.FindAll("tool", string.Join(':', Dir("real"), Dir("real"), linkDir));
        Assert.Equal([real], result);
        // On its own the link is a valid result.
        Assert.Equal([link], _locator.FindAll("tool", linkDir));
    }

    [Fact]
    public void Name_with_slash_is_checked_as_path()
    {
        if (OperatingSystem.IsWindows()) return;
        var tool = File_(Dir("a"), "tool", Exec);
        var plain = File_(Dir("a"), "plain", NoExec);
        Assert.Equal([tool], _locator.FindAll(tool, ""));
        Assert.Empty(_locator.FindAll(plain, null));
    }

    [Fact]
    public void Null_or_empty_path_finds_nothing()
    {
        Assert.Empty(_locator.FindAll("tool", null));
        Assert.Empty(_locator.FindAll("tool", ""));
    }
}
