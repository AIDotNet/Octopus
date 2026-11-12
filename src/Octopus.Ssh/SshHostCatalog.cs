namespace Octopus.Ssh;

/// <summary>The SSH hosts of this computer, from ~/.ssh/config. The file is small, so it is read again on every refresh and nothing is cached.</summary>
public interface ISshHostCatalog
{
    /// <summary>The result of the last <see cref="Refresh"/>.</summary>
    SshHostScan Current { get; }

    /// <summary>Raised after every <see cref="Refresh"/>.</summary>
    event EventHandler? Changed;

    /// <summary>Reads the configuration file again.</summary>
    void Refresh();

    /// <summary>
    /// Adds the host as a Host block of the configuration file (created with its folder when missing). Throws
    /// <see cref="ArgumentException"/> for a draft that is not valid, and an IO exception when the file cannot be written.
    /// </summary>
    void Add(SshHostDraft draft);
}

public sealed class SshHostCatalog : ISshHostCatalog
{
    private readonly string _home;
    private readonly string _configPath;

    /// <param name="homeDirectory">The user's home folder: where <c>~/.ssh/config</c> is and what <c>~</c> stands for.</param>
    public SshHostCatalog(string homeDirectory)
    {
        _home = homeDirectory;
        _configPath = Path.Combine(homeDirectory, ".ssh", "config");
        Refresh();
    }

    /// <summary>The home folder of the current user.</summary>
    public static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public SshHostScan Current { get; private set; } = new(string.Empty, false, default, []);

    public event EventHandler? Changed;

    public void Refresh()
    {
        var exists = File.Exists(_configPath);
        IReadOnlyList<SshHost> hosts = exists ? SshConfigParser.Parse(_configPath, _home) : [];
        Current = new SshHostScan(_configPath, exists, DateTimeOffset.UtcNow, hosts);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Add(SshHostDraft draft)
    {
        // Checked against the file as it is now, which may have been edited by hand since the last look.
        Refresh();
        var issues = draft.Validate(Current.Hosts.Select(h => h.Alias));
        if (issues.Count > 0)
        {
            throw new ArgumentException("The host is not valid: " + string.Join(", ", issues.Select(i => $"{i.Field} {i.Problem}")), nameof(draft));
        }

        var directory = Path.GetDirectoryName(_configPath)!;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        var config = File.Exists(_configPath) ? File.ReadAllText(_configPath) : "";
        File.WriteAllText(_configPath, SshConfigWriter.Insert(config, draft));
        Refresh();
    }
}
