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

    [SupportedOSPlatform("windows")]
    private static string? RegistryEnv(string name)
    {
        using var user = Registry.CurrentUser.OpenSubKey("Environment");
        if (user?.GetValue(name) is string userValue && !string.IsNullOrWhiteSpace(userValue))
            return userValue;
        using var machine = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        return machine?.GetValue(name) is string machineValue && !string.IsNullOrWhiteSpace(machineValue)
            ? machineValue
            : null;
    }

    private static string? GetEnv(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        if (OperatingSystem.IsWindows())
        {
            try { return RegistryEnv(name); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return null;
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
            yield return Path.Combine(local, "omp");
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links");
        }
        foreach (var dir in MiseDirectories(
            home,
            local,
            GetEnv,
            Directory.Exists,
            directory => Directory.Exists(directory) ? Directory.GetDirectories(directory) : []))
        {
            yield return dir;
        }
    }

    /// <summary>
    /// Existing <c>mise</c> directories on Windows (shims, bin, newest Node installs and npm-backend tools), in
    /// priority order without duplicates. Used by both discovery and terminal launches so agents found through mise
    /// can also run when the shell profile does not activate mise.
    /// </summary>
    internal static IReadOnlyList<string> MiseDirectories(
        string? home,
        string? localAppData,
        Func<string, string?> getEnv,
        Func<string, bool> directoryExists,
        Func<string, IReadOnlyList<string>> subdirectories)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            var normalized = directory.TrimEnd('\\', '/');
            if (!seen.Add(normalized)) return;
            bool exists;
            try { exists = directoryExists(directory); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            if (exists) result.Add(directory);
        }

        void AddVersioned(string root, string suffix)
        {
            IReadOnlyList<string> versions;
            try { versions = subdirectories(root); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var version in versions.OrderByDescending(VersionKey, StringComparer.Ordinal))
                Add(suffix.Length == 0 ? version : Path.Combine(version, suffix));
        }

        void AddMise(string? dataDir)
        {
            if (string.IsNullOrWhiteSpace(dataDir)) return;
            Add(Path.Combine(dataDir, "shims"));
            Add(Path.Combine(dataDir, "bin"));
            var installs = Path.Combine(dataDir, "installs");
            var nodeInstalls = Path.Combine(installs, "node");
            AddVersioned(nodeInstalls, "");
            AddVersioned(nodeInstalls, "bin");

            IReadOnlyList<string> tools;
            try { tools = subdirectories(installs); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
            foreach (var tool in tools)
            {
                var toolName = FileNameOf(tool);
                if (toolName.StartsWith("npm-", StringComparison.OrdinalIgnoreCase))
                {
                    AddVersioned(tool, Path.Combine("node_modules", ".bin"));
                    AddVersioned(tool, "bin");
                    AddVersioned(tool, "");
                }
            }
        }

        AddMise(getEnv("MISE_DATA_DIR"));
        var xdgDataHome = getEnv("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome)) AddMise(Path.Combine(xdgDataHome, "mise"));
        if (!string.IsNullOrWhiteSpace(localAppData)) AddMise(Path.Combine(localAppData, "mise"));
        if (!string.IsNullOrWhiteSpace(home)) AddMise(Path.Combine(home, ".local", "share", "mise"));
        return result;
    }

    private static string VersionKey(string directory)
    {
        var name = FileNameOf(directory);
        var sb = new System.Text.StringBuilder();
        var digits = 0;
        foreach (var c in name)
        {
            if (char.IsDigit(c))
            {
                digits++;
                sb.Append(c);
                continue;
            }
            if (digits > 0) sb.Insert(sb.Length - digits, new string('0', Math.Max(0, 8 - digits)));
            digits = 0;
            sb.Append(c);
        }
        if (digits > 0) sb.Insert(sb.Length - digits, new string('0', Math.Max(0, 8 - digits)));
        return sb.ToString();
    }

    private static string FileNameOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var slash = trimmed.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }
}
