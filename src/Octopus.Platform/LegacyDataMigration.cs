namespace Octopus.Platform;

/// <summary>
/// One-time non-destructive copy of desktop app data from a previous platform-specific directory
/// (such as <c>~/Library/Application Support/Octopus</c>, <c>%AppData%\Octopus</c>, or <c>~/.config/octopus</c>)
/// into <c>~/.octopus</c>. Never overwrites existing files and ignores non-data folders already under
/// <c>~/.octopus</c> (such as <c>ssh/</c>, <c>workspaces/</c>, and <c>logs/</c>).
/// </summary>
public static class LegacyDataMigration
{
    private static readonly string[] CoreMarkers =
    [
        "projects.json",
        "settings.json",
        "accounts.json",
        "agent-accounts.json",
        "plugins.json",
        "remote",
        "accounts",
        "agents",
        "plugin-data",
        "project-icons",
        "claude-statusline",
        "handoffs",
    ];

    /// <summary>
    /// Copies legacy files into <paramref name="targetDirectory"/> when <paramref name="legacyDirectory"/> contains
    /// app data and <paramref name="targetDirectory"/> does not yet have any core app data entries. Never throws.
    /// </summary>
    public static void MigrateIfNeeded(string targetDirectory, string? legacyDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(legacyDirectory) || !Directory.Exists(legacyDirectory))
            {
                return;
            }

            var targetFull = Path.GetFullPath(targetDirectory);
            var legacyFull = Path.GetFullPath(legacyDirectory);
            var comparison = OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (string.Equals(
                Path.TrimEndingDirectorySeparator(targetFull),
                Path.TrimEndingDirectorySeparator(legacyFull),
                comparison))
            {
                return;
            }

            if (HasAnyCoreData(targetFull) || !HasAnyCoreData(legacyFull))
            {
                return;
            }

            Directory.CreateDirectory(targetFull);
            CopyMissingRecursive(legacyFull, targetFull);
        }
        catch
        {
            // Migration is best-effort and must never block app startup.
        }
    }

    private static bool HasAnyCoreData(string directory)
    {
        if (!Directory.Exists(directory)) return false;
        foreach (var entry in CoreMarkers)
        {
            var path = Path.Combine(directory, entry);
            if (File.Exists(path) || Directory.Exists(path))
            {
                return true;
            }
        }
        return false;
    }

    private static void CopyMissingRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LinkTarget is not null) continue;
                var dest = Path.Combine(targetDir, info.Name);
                if (!File.Exists(dest) && !Directory.Exists(dest))
                {
                    File.Copy(file, dest, overwrite: false);
                }
            }
            catch
            {
                // Skip unreadable or racing files and continue migrating the rest.
            }
        }

        foreach (var subDir in Directory.EnumerateDirectories(sourceDir))
        {
            try
            {
                var info = new DirectoryInfo(subDir);
                if (info.LinkTarget is not null) continue;
                var dest = Path.Combine(targetDir, info.Name);
                if (!File.Exists(dest))
                {
                    CopyMissingRecursive(subDir, dest);
                }
            }
            catch
            {
                // Skip unreadable directories and continue migrating the rest.
            }
        }
    }
}
