namespace Octopus.Platform.Windows;

/// <summary>
/// The newest PowerShell 7+ (pwsh) found on PATH or in its usual install folders, then Windows PowerShell, then cmd.
/// Not verified on real Windows.
/// </summary>
public sealed class WindowsShellResolver(IExecutableLocator locator) : IShellResolver
{
    private readonly Lazy<ShellInfo> _default = new(() => Resolve(locator));

    public ShellInfo GetDefaultShell() => _default.Value;

    public ShellInfo? Describe(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        var kind = name switch
        {
            "pwsh" or "powershell" => ShellKind.PowerShell,
            "cmd" => ShellKind.Cmd,
            "fish" => ShellKind.Fish,
            _ => ShellKind.Posix, // bash.exe from Git for Windows, MSYS, WSL launchers
        };
        return new ShellInfo(path, name, kind);
    }

    public IReadOnlyList<ShellInfo> GetAvailableShells()
    {
        var pathVariable = WindowsLoginEnvironment.MergePath(Environment.GetEnvironmentVariable("PATH"));
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var result = new List<ShellInfo> { GetDefaultShell() };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { result[0].Path };
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Describe(path) is { } info && seen.Add(info.Path)) result.Add(info);
        }

        // Every PowerShell 7+, newest first, then the one that ships with Windows.
        var pwsh = locator.FindAll("pwsh.exe", pathVariable).Concat(PowerShellInstallations())
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(VersionOf);
        foreach (var path in pwsh) Add(path);
        Add(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"));
        Add(Environment.GetEnvironmentVariable("ComSpec"));
        Add(Path.Combine(system, "cmd.exe"));

        // Git Bash and other Unix-style shells. The bash.exe in System32 is the WSL launcher, not a shell for this.
        foreach (var root in new[]
        {
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        }.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Add(Path.Combine(root!, "Git", "bin", "bash.exe"));
        }
        foreach (var path in locator.FindAll("nu.exe", pathVariable)) Add(path);
        foreach (var path in locator.FindAll("fish.exe", pathVariable)) Add(path);
        foreach (var path in locator.FindAll("bash.exe", pathVariable).Where(p => !p.StartsWith(system, StringComparison.OrdinalIgnoreCase)))
            Add(path);
        return result;
    }

    public IReadOnlyList<string> GetInteractiveArguments(ShellInfo shell) =>
        shell.Kind == ShellKind.PowerShell ? ["-NoLogo"] : shell.Kind == ShellKind.Cmd ? [] : ["-l"];

    public IReadOnlyList<string> GetCommandArguments(ShellInfo shell, string commandLine) => shell.Kind switch
    {
        ShellKind.PowerShell => ["-NoLogo", "-Command", commandLine],
        ShellKind.Cmd => ["/d", "/s", "/c", commandLine],
        _ => ["-l", "-i", "-c", commandLine],
    };

    private static ShellInfo Resolve(IExecutableLocator locator)
    {
        // The PATH this process inherited can predate the user's last install; the registry's is read fresh.
        var pathVariable = WindowsLoginEnvironment.MergePath(Environment.GetEnvironmentVariable("PATH"));
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var resolver = new WindowsShellResolver(locator);

        // PowerShell 7+ (pwsh) wins over Windows PowerShell 5.1, and of several installs the newest one wins.
        var pwsh = locator.FindAll("pwsh.exe", pathVariable).Concat(PowerShellInstallations()).ToList();
        if (Newest(pwsh) is { } latest && resolver.Describe(latest) is { } latestInfo)
            return latestInfo;

        var candidates = new List<string>();
        candidates.AddRange(locator.FindAll("powershell.exe", pathVariable));
        candidates.Add(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"));
        candidates.Add(Environment.GetEnvironmentVariable("ComSpec") ?? "");
        candidates.AddRange(locator.FindAll("cmd.exe", pathVariable));
        candidates.Add(Path.Combine(system, "cmd.exe"));
        foreach (var candidate in candidates)
        {
            var info = resolver.Describe(candidate);
            if (info is not null) return info;
        }
        return new ShellInfo(Path.Combine(system, "cmd.exe"), "cmd", ShellKind.Cmd);
    }

    /// <summary>Where pwsh lives when it is not on PATH: the MSI folders, the Store and WinGet aliases, the dotnet tool.</summary>
    private static IEnumerable<string> PowerShellInstallations()
    {
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
        };
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.Combine(root!, "PowerShell");
            if (!Directory.Exists(folder)) continue;
            // Folders "7", "7.5" and so on; "7-preview" is a preview build and is not picked over a release.
            foreach (var version in Directory.EnumerateDirectories(folder).Where(d => char.IsDigit(Path.GetFileName(d)[0]) && !Path.GetFileName(d).Contains('-')))
                yield return Path.Combine(version, "pwsh.exe");
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length > 0)
        {
            yield return Path.Combine(local, "Microsoft", "WindowsApps", "pwsh.exe");
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "pwsh.exe");
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0)
            yield return Path.Combine(home, ".dotnet", "tools", "pwsh.exe");
    }

    /// <summary>The existing candidate with the highest file version; an alias that has no version rates 0 and is picked only when nothing else exists.</summary>
    private static string? Newest(IEnumerable<string> candidates)
    {
        string? best = null;
        var bestVersion = new Version(-1, -1);
        foreach (var candidate in candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var version = VersionOf(candidate);
            if (best is null || version > bestVersion)
            {
                best = candidate;
                bestVersion = version;
            }
        }
        return best;
    }

    private static Version VersionOf(string path)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new Version(0, 0);
        }
    }
}
