namespace Octopus.Platform.Windows;

/// <summary>
/// pwsh (PATH or Program Files), then Windows PowerShell, then cmd. Not verified on real Windows.
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
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);

        var candidates = new List<string>();
        candidates.AddRange(locator.FindAll("pwsh.exe", pathVariable));
        candidates.Add(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
        candidates.AddRange(locator.FindAll("powershell.exe", pathVariable));
        candidates.Add(Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"));
        candidates.Add(Environment.GetEnvironmentVariable("ComSpec") ?? "");
        candidates.AddRange(locator.FindAll("cmd.exe", pathVariable));
        candidates.Add(Path.Combine(system, "cmd.exe"));

        var resolver = new WindowsShellResolver(locator);
        foreach (var candidate in candidates)
        {
            var info = resolver.Describe(candidate);
            if (info is not null) return info;
        }
        return new ShellInfo(Path.Combine(system, "cmd.exe"), "cmd", ShellKind.Cmd);
    }
}
