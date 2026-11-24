using System.Text;
using System.Text.Json;
using Octopus.Platform;

namespace Octopus.Agents.Signals;

/// <summary>Summary of what <see cref="SignalHookCleanup"/> removed or restored.</summary>
public sealed record HookCleanupResult(
    bool AgyHooksRemoved,
    int ClaudeStatusLinesRestored,
    bool SignalAttachCacheRemoved,
    bool SignalHubCacheRemoved,
    bool SignalThrottleRemoved)
{
    public bool AnyCleaned =>
        AgyHooksRemoved
        || ClaudeStatusLinesRestored > 0
        || SignalAttachCacheRemoved
        || SignalHubCacheRemoved
        || SignalThrottleRemoved;
}

/// <summary>
/// Removes hooks, bridges, and generated signal files created by Octopus when the application is uninstalled.
/// Never creates Octopus's data or cache directories if they do not already exist, and never throws on I/O errors
/// so uninstallation cannot be blocked by a locked or unwritable file.
/// </summary>
public static class SignalHookCleanup
{
    public static HookCleanupResult CleanAll(
        string home,
        string? dataDirectory,
        string? cacheDirectory,
        string? claudeConfigDir = null,
        string? throttleDirectory = null,
        IEnumerable<string>? additionalDataDirectories = null)
    {
        var agyRemoved = !string.IsNullOrWhiteSpace(home) && RemoveAgyHooks(home);
        var claudeRestored = RestoreClaudeStatusLines(home, dataDirectory, claudeConfigDir);
        if (additionalDataDirectories is not null)
        {
            foreach (var extra in additionalDataDirectories)
            {
                if (!string.IsNullOrWhiteSpace(extra)
                    && !string.Equals(extra, dataDirectory, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                {
                    claudeRestored += RestoreClaudeStatusLines(home, extra, claudeConfigDir);
                }
            }
        }
        var attachRemoved = DeleteDirectoryIfExists(
            string.IsNullOrWhiteSpace(cacheDirectory) ? null : Path.Combine(cacheDirectory, SignalAttachPlanner.AttachDirectoryName));
        var hubRemoved = DeleteDirectoryIfExists(
            string.IsNullOrWhiteSpace(cacheDirectory) ? null : Path.Combine(cacheDirectory, SignalHub.RootDirectoryName));
        var throttleRemoved = DeleteDirectoryIfExists(throttleDirectory ?? DefaultThrottleDirectory());

        return new HookCleanupResult(agyRemoved, claudeRestored, attachRemoved, hubRemoved, throttleRemoved);
    }

    /// <summary>
    /// Removes the <c>octopus</c> entry from <c>~/.gemini/config/hooks.json</c>. Preserves every other entry, leaves
    /// malformed files untouched, and deletes <c>hooks.json</c> when <c>octopus</c> was its only entry.
    /// </summary>
    public static bool RemoveAgyHooks(string home)
    {
        if (string.IsNullOrWhiteSpace(home))
        {
            return false;
        }
        var path = Path.Combine(home, ".gemini", "config", "hooks.json");
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            var existing = File.ReadAllText(path);
            var removal = SignalAttachContent.RemoveAgyHooks(existing);
            switch (removal.Kind)
            {
                case AgyHookRemovalKind.DeleteFile:
                    File.Delete(path);
                    return true;
                case AgyHookRemovalKind.Updated when removal.Content is not null:
                    var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    File.WriteAllText(temp, removal.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    File.Move(temp, path, overwrite: true);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Restores every Claude Code configuration directory where <see cref="ClaudeStatusLineBridge"/> is installed, and
    /// removes Octopus's bridge script directory once nothing remains installed.
    /// </summary>
    public static int RestoreClaudeStatusLines(string home, string? dataDirectory, string? claudeConfigDir = null)
    {
        var bridge = new ClaudeStatusLineBridge(new NoCreateAppPaths(dataDirectory ?? ""));
        var candidates = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                return;
            }
            string full;
            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return;
            }
            if (seen.Add(full))
            {
                candidates.Add(full);
            }
        }

        if (!string.IsNullOrWhiteSpace(dataDirectory) && Directory.Exists(bridge.Directory))
        {
            foreach (var dir in bridge.InstalledDirectories())
            {
                Add(dir);
            }
        }
        if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home))
        {
            foreach (var dir in ClaudeConfigDirectories.Find(home, claudeConfigDir))
            {
                Add(dir);
            }
        }
        else if (!string.IsNullOrWhiteSpace(claudeConfigDir) && Directory.Exists(claudeConfigDir))
        {
            Add(claudeConfigDir);
        }

        var restored = 0;
        var failed = false;
        foreach (var dir in candidates)
        {
            try
            {
                if (!bridge.IsInstalled(dir))
                {
                    continue;
                }
                bridge.Restore(dir);
                restored++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                failed = true;
            }
        }

        if (!failed
            && !string.IsNullOrWhiteSpace(dataDirectory)
            && Directory.Exists(bridge.Directory)
            && bridge.InstalledDirectories().Count == 0)
        {
            DeleteDirectoryIfExists(bridge.Directory);
        }

        return restored;
    }

    private static string DefaultThrottleDirectory() =>
        Path.Combine(Path.GetTempPath(), $"octopus-signal-{Environment.UserName}");

    private static bool DeleteDirectoryIfExists(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }
        try
        {
            if (!Directory.Exists(directory))
            {
                return false;
            }
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>An <see cref="IAppPaths"/> that never creates missing directories during uninstall.</summary>
    private sealed class NoCreateAppPaths(string dataDirectory) : IAppPaths
    {
        public string DataDirectory => dataDirectory;
        public string CacheDirectory => "";
    }
}
