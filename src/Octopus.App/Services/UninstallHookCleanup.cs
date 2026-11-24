using Octopus.Agents.Signals;
using Octopus.Platform;

namespace Octopus.App.Services;

/// <summary>
/// Handles the headless <c>--cleanup-hooks</c> invocation run by uninstallers (Inno Setup on Windows,
/// <c>uninstall.sh</c> on Linux, or manually on macOS / portable installs) before the binaries are deleted.
/// Runs before Avalonia initializes so it works without a display server and never creates missing app directories.
/// </summary>
public static class UninstallHookCleanup
{
    public const string CliFlag = "--cleanup-hooks";

    public static bool IsRequested(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, CliFlag, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// If <paramref name="args"/> asks for hook cleanup, performs best-effort cleanup and returns true so
    /// <see cref="Program.Main"/> exits immediately without starting the GUI.
    /// </summary>
    public static bool TryHandleCommandLine(string[] args)
    {
        if (!IsRequested(args))
        {
            return false;
        }
        try
        {
            RunForCurrentUser();
        }
        catch
        {
            // Never fail or hang an uninstaller.
        }
        return true;
    }

    public static HookCleanupResult RunForCurrentUser(
        string? homeOverride = null,
        string? dataDirOverride = null,
        string? cacheDirOverride = null,
        string? claudeConfigDirOverride = null,
        string? throttleDirOverride = null)
    {
        var home = ResolveHome(homeOverride);
        var (dataDir, legacyDataDir, cacheDir) = ResolveDirectories(home, dataDirOverride, cacheDirOverride);
        var claudeConfigDir = claudeConfigDirOverride ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        IReadOnlyList<string>? additionalDataDirs = string.IsNullOrWhiteSpace(legacyDataDir) ? null : [legacyDataDir];
        return SignalHookCleanup.CleanAll(home, dataDir, cacheDir, claudeConfigDir, throttleDirOverride, additionalDataDirs);
    }

    internal static (string DataDirectory, string? LegacyDataDirectory, string CacheDirectory) ResolveDirectories(
        string home,
        string? dataDirOverride = null,
        string? cacheDirOverride = null)
    {
        var isolated = dataDirOverride ?? Environment.GetEnvironmentVariable(FixedAppPaths.Variable);
        if (!string.IsNullOrWhiteSpace(isolated))
        {
            var root = Path.GetFullPath(isolated);
            return (root, null, cacheDirOverride ?? Path.Combine(root, "cache"));
        }

        var dataDir = Path.Combine(home, ".octopus");

        if (OperatingSystem.IsMacOS())
        {
            return (
                dataDir,
                Path.Combine(home, "Library", "Application Support", "Octopus"),
                cacheDirOverride ?? Path.Combine(home, "Library", "Caches", "Octopus"));
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var legacy = string.IsNullOrWhiteSpace(appData) ? null : Path.Combine(appData, "Octopus");
            return (
                dataDir,
                legacy,
                cacheDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Octopus", "Cache"));
        }

        var xdgConfig = Absolute(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")) ?? Path.Combine(home, ".config");
        var xdgCache = Absolute(Environment.GetEnvironmentVariable("XDG_CACHE_HOME")) ?? Path.Combine(home, ".cache");
        return (
            dataDir,
            Path.Combine(xdgConfig, "octopus"),
            cacheDirOverride ?? Path.Combine(xdgCache, "octopus"));
    }

    private static string ResolveHome(string? homeOverride)
    {
        if (!string.IsNullOrWhiteSpace(homeOverride))
        {
            return homeOverride;
        }
        if (OperatingSystem.IsWindows())
        {
            var userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
            return !string.IsNullOrWhiteSpace(userProfile)
                ? userProfile
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        return Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private static string? Absolute(string? path) =>
        !string.IsNullOrEmpty(path) && Path.IsPathRooted(path) ? path : null;
}
