using System.Runtime.Versioning;
using Microsoft.Win32;
using Octopus.Platform;

namespace Octopus.Platform.Windows;

/// <summary>
/// Windows has no login shell to ask, but the PATH the app inherited can be stale: Explorer hands every program the
/// environment it had at sign-in, so an agent installed afterwards (npm, scoop, bun, an installer script) is on the
/// user's PATH in the registry and in a new terminal, yet missing here. The PATH that is returned is the inherited
/// one followed by the registry's machine and user PATH and the folders those installers usually write to.
/// </summary>
public sealed class WindowsLoginEnvironment : ILoginEnvironment
{
    public Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default)
    {
        var env = HostEnvironmentFilter.SnapshotProcessEnvironment(StringComparer.OrdinalIgnoreCase);
        try
        {
            env["PATH"] = MergePath(env.TryGetValue("PATH", out var inherited) ? inherited : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Keep the inherited PATH.
        }
        return Task.FromResult<IReadOnlyDictionary<string, string>>(env);
    }

    /// <summary>The inherited PATH, then the registry's, then the usual install folders, without repeats.</summary>
    internal static string MergePath(string? inherited)
    {
        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddAll(IEnumerable<string> candidates)
        {
            foreach (var raw in candidates)
            {
                var directory = Environment.ExpandEnvironmentVariables(raw.Trim()).Trim('"');
                if (directory.Length > 0 && seen.Add(directory.TrimEnd('\\', '/')))
                    directories.Add(directory);
            }
        }

        AddAll((inherited ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        if (OperatingSystem.IsWindows())
        {
            AddAll(RegistryPath(Registry.CurrentUser, "Environment"));
            AddAll(RegistryPath(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"));
        }
        AddAll(WellKnownDirectories().Where(Directory.Exists));
        return string.Join(';', directories);
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegistryPath(RegistryKey root, string subKey)
    {
        using var key = root.OpenSubKey(subKey);
        return (key?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
    }

    private static IEnumerable<string> WellKnownDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (home.Length > 0)
        {
            yield return Path.Combine(home, ".local", "bin");
            yield return Path.Combine(home, ".opencode", "bin");
            yield return Path.Combine(home, ".bun", "bin");
            yield return Path.Combine(home, ".cargo", "bin");
            yield return Path.Combine(home, "scoop", "shims");
        }
        if (roaming.Length > 0)
            yield return Path.Combine(roaming, "npm");
        if (local.Length > 0)
        {
            yield return Path.Combine(local, "Volta", "bin");
            yield return Path.Combine(local, "pnpm");
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links");
        }
    }
}
