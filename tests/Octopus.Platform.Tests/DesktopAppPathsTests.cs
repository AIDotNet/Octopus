using Octopus.Platform.Linux;
using Octopus.Platform.MacOS;
using Octopus.Platform.Windows;

namespace Octopus.Platform.Tests;

public sealed class DesktopAppPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "octopus-desktop-paths-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { }
        }
    }

    [Fact]
    public void Mac_Windows_and_Linux_default_data_directory_to_dot_octopus_under_home()
    {
        var macHome = Path.Combine(_root, "mac");
        var winHome = Path.Combine(_root, "win");
        var linuxHome = Path.Combine(_root, "linux");

        var mac = new MacAppPaths(macHome);
        var win = new WindowsAppPaths(winHome, Path.Combine(winHome, "AppData", "Roaming"), Path.Combine(winHome, "AppData", "Local"));
        var linux = new LinuxAppPaths(linuxHome, Path.Combine(linuxHome, ".config"), Path.Combine(linuxHome, ".cache"));

        Assert.Equal(Path.Combine(macHome, ".octopus"), mac.DataDirectory);
        Assert.Equal(Path.Combine(macHome, "Library", "Caches", "Octopus"), mac.CacheDirectory);

        Assert.Equal(Path.Combine(winHome, ".octopus"), win.DataDirectory);
        Assert.Equal(Path.Combine(winHome, "AppData", "Local", "Octopus", "Cache"), win.CacheDirectory);

        Assert.Equal(Path.Combine(linuxHome, ".octopus"), linux.DataDirectory);
        Assert.Equal(Path.Combine(linuxHome, ".cache", "octopus"), linux.CacheDirectory);
    }

    [Fact]
    public void Mac_migrates_legacy_application_support_data_even_when_ssh_and_workspaces_already_exist()
    {
        var home = Path.Combine(_root, "migrate-mac");
        var legacy = Path.Combine(home, "Library", "Application Support", "Octopus");
        var target = Path.Combine(home, ".octopus");

        Directory.CreateDirectory(Path.Combine(legacy, "remote"));
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"theme\":\"dark\"}");
        File.WriteAllText(Path.Combine(legacy, "projects.json"), "{\"projects\":[]}");
        File.WriteAllText(Path.Combine(legacy, "remote", "hosts.json"), "{\"hosts\":[]}");

        // Existing ~/.octopus/ssh, workspaces, and logs should remain untouched and must not block migration.
        Directory.CreateDirectory(Path.Combine(target, "ssh"));
        Directory.CreateDirectory(Path.Combine(target, "workspaces"));
        Directory.CreateDirectory(Path.Combine(target, "logs"));
        File.WriteAllText(Path.Combine(target, "logs", "crash-1.log"), "keep-me");

        var paths = new MacAppPaths(home);
        Assert.Equal(target, paths.DataDirectory);

        Assert.Equal("{\"theme\":\"dark\"}", File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.Equal("{\"projects\":[]}", File.ReadAllText(Path.Combine(target, "projects.json")));
        Assert.Equal("{\"hosts\":[]}", File.ReadAllText(Path.Combine(target, "remote", "hosts.json")));
        Assert.Equal("keep-me", File.ReadAllText(Path.Combine(target, "logs", "crash-1.log")));
        // Legacy copy stays intact so rollback or running processes are never broken.
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
    }

    [Fact]
    public void Migration_does_not_overwrite_existing_core_data_in_dot_octopus()
    {
        var home = Path.Combine(_root, "no-overwrite");
        var legacy = Path.Combine(home, ".config", "octopus");
        var target = Path.Combine(home, ".octopus");

        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "legacy");
        File.WriteAllText(Path.Combine(legacy, "projects.json"), "legacy-projects");

        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "settings.json"), "current");

        var paths = new LinuxAppPaths(home, Path.Combine(home, ".config"), Path.Combine(home, ".cache"));
        Assert.Equal(target, paths.DataDirectory);

        Assert.Equal("current", File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.False(File.Exists(Path.Combine(target, "projects.json")));
    }
}
