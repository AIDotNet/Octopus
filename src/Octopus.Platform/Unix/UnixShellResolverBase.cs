namespace Octopus.Platform.Unix;

/// <summary>
/// Shell handling shared by macOS and Linux. Subclasses only say where to look for the user's login shell.
/// </summary>
public abstract class UnixShellResolverBase : IShellResolver
{
    /// <summary>Accounts that exist but cannot host an interactive session.</summary>
    private static readonly HashSet<string> Rejected = new(StringComparer.Ordinal)
    {
        "nologin", "false", "true", "rbash", "git-shell", "tmux", "screen", "sync", "halt", "shutdown",
    };

    private readonly Lazy<ShellInfo> _default;

    protected UnixShellResolverBase() => _default = new Lazy<ShellInfo>(Resolve);

    public ShellInfo GetDefaultShell() => _default.Value;

    /// <summary>Candidate shell paths in priority order. Null or empty entries are skipped.</summary>
    protected abstract IEnumerable<string?> EnumerateCandidates();

    /// <summary>Shell used when no candidate works; must exist on every machine of the platform.</summary>
    protected abstract string LastResortPath { get; }

    protected static string? ShellFromEnvironment() => Environment.GetEnvironmentVariable("SHELL");

    public ShellInfo? Describe(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !UnixFile.IsExecutableFile(path)) return null;
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (Rejected.Contains(name)) return null;
        var kind = name switch { "fish" => ShellKind.Fish, "pwsh" => ShellKind.PowerShell, _ => ShellKind.Posix };
        return new ShellInfo(path, name, kind);
    }

    public IReadOnlyList<string> GetInteractiveArguments(ShellInfo shell) =>
        shell.Kind == ShellKind.PowerShell ? ["-Login"] : ["-l"];

    public IReadOnlyList<string> GetCommandArguments(ShellInfo shell, string commandLine) =>
        shell.Kind == ShellKind.PowerShell
            ? ["-Login", "-Command", commandLine]
            : ["-l", "-i", "-c", commandLine];

    private ShellInfo Resolve()
    {
        foreach (var candidate in EnumerateCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var info = Describe(candidate.Trim());
            if (info is not null) return info;
        }
        return Describe(LastResortPath) ?? new ShellInfo(LastResortPath, Path.GetFileName(LastResortPath), ShellKind.Posix);
    }
}
