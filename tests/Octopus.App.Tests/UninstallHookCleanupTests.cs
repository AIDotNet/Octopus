using System.Diagnostics;
using System.Text.Json;
using Octopus.App.Services;
using Xunit;

namespace Octopus.App.Tests;

public sealed class UninstallHookCleanupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("octopus-uninstall-hooks-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void IsRequested_matches_cleanup_flag_only()
    {
        Assert.False(UninstallHookCleanup.IsRequested([]));
        Assert.False(UninstallHookCleanup.IsRequested(["octopus://auth-done"]));
        Assert.True(UninstallHookCleanup.IsRequested(["--cleanup-hooks"]));
        Assert.True(UninstallHookCleanup.IsRequested(["--CLEANUP-HOOKS"]));
    }

    [Fact]
    public void RunForCurrentUser_cleans_hooks_and_cache_without_creating_missing_directories()
    {
        var home = Directory.CreateDirectory(Path.Combine(_root, "home")).FullName;
        var dataDir = Path.Combine(_root, "isolated");
        var cacheDir = Path.Combine(dataDir, "cache");
        var throttleDir = Path.Combine(_root, "throttle");

        // Fresh state: nothing exists and nothing should be created.
        var empty = UninstallHookCleanup.RunForCurrentUser(home, dataDir, throttleDirOverride: throttleDir);
        Assert.False(empty.AnyCleaned);
        Assert.False(Directory.Exists(dataDir));
        Assert.False(Directory.Exists(cacheDir));

        // Seed agy hook and signal-attach cache.
        var hooksPath = Path.Combine(home, ".gemini", "config", "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
        File.WriteAllText(hooksPath, """{"octopus":{"Stop":[]},"keep":{"Stop":[]}}""");
        var attachDir = Directory.CreateDirectory(Path.Combine(cacheDir, "signal-attach")).FullName;
        File.WriteAllText(Path.Combine(attachDir, "claude-settings.json"), "{}");

        var cleaned = UninstallHookCleanup.RunForCurrentUser(home, dataDir, throttleDirOverride: throttleDir);

        Assert.True(cleaned.AgyHooksRemoved);
        Assert.True(cleaned.SignalAttachCacheRemoved);
        Assert.False(Directory.Exists(attachDir));
        var hooks = JsonElement.Parse(File.ReadAllText(hooksPath));
        Assert.False(hooks.TryGetProperty("octopus", out _));
        Assert.True(hooks.TryGetProperty("keep", out _));
    }

    [Fact]
    public void ResolveDirectories_uses_dot_octopus_and_keeps_platform_legacy_data_directory()
    {
        var home = Path.Combine(_root, "user-home");
        var (dataDir, legacyDataDir, cacheDir) = UninstallHookCleanup.ResolveDirectories(home, dataDirOverride: "");

        Assert.Equal(Path.Combine(home, ".octopus"), dataDir);
        Assert.False(string.IsNullOrWhiteSpace(legacyDataDir));
        Assert.False(string.IsNullOrWhiteSpace(cacheDir));
        Assert.False(Directory.Exists(dataDir));
    }

    [Fact]
    public async Task Linux_uninstall_script_invokes_cleanup_hooks_before_removing_install_dir()
    {
        if (OperatingSystem.IsWindows()) return;

        var repoRoot = FindRepoRoot();
        var uninstallSource = Path.Combine(repoRoot, "packaging", "linux", "uninstall.sh");
        Assert.True(File.Exists(uninstallSource), $"Expected {uninstallSource}");

        var installDir = Directory.CreateDirectory(Path.Combine(_root, "install")).FullName;
        var argsLog = Path.Combine(_root, "octopus-args.log");
        var fakeBinary = Path.Combine(installDir, "Octopus");
        File.WriteAllText(fakeBinary, $"#!/bin/sh\nprintf '%s\\n' \"$@\" > '{argsLog}'\n");
        File.SetUnixFileMode(fakeBinary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var uninstallCopy = Path.Combine(installDir, "uninstall.sh");
        File.Copy(uninstallSource, uninstallCopy);
        File.SetUnixFileMode(uninstallCopy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(installDir, ".octopus-install"), "link=\nentry=\nversion=1.0.0\n");

        using var process = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { uninstallCopy } })!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.False(Directory.Exists(installDir));
        Assert.True(File.Exists(argsLog));
        Assert.Equal(["--cleanup-hooks"], File.ReadAllLines(argsLog));
    }

    [Fact]
    public async Task App_process_handles_cleanup_hooks_headlessly_and_exits_zero()
    {
        var home = Directory.CreateDirectory(Path.Combine(_root, "proc-home")).FullName;
        var dataDir = Path.Combine(_root, "proc-data");
        var hooksPath = Path.Combine(home, ".gemini", "config", "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
        File.WriteAllText(hooksPath, """{"octopus":{"Stop":[]},"keep":{"Stop":[]}}""");
        var attachDir = Directory.CreateDirectory(Path.Combine(dataDir, "cache", "signal-attach")).FullName;
        File.WriteAllText(Path.Combine(attachDir, "file.json"), "{}");

        var appDll = typeof(Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { appDll, UninstallHookCleanup.CliFlag },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment["HOME"] = home;
        start.Environment["USERPROFILE"] = home;
        start.Environment["OCTOPUS_DATA_DIR"] = dataDir;

        using var process = Process.Start(start)!;
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        Assert.False(Directory.Exists(attachDir));
        var hooks = JsonElement.Parse(File.ReadAllText(hooksPath));
        Assert.False(hooks.TryGetProperty("octopus", out _));
        Assert.True(hooks.TryGetProperty("keep", out _));
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Octopus.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate Octopus.slnx above test output directory.");
    }
}
