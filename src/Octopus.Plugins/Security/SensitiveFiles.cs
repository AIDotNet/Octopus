using Octopus.FileSystem;

namespace Octopus.Plugins.Security;

/// <summary>
/// The name lists of the protection rules (design/plugins/04 §3.3, 11 §4.3 and §8.1), in one place so they can be
/// reviewed together. All name matching ignores case on every platform: on a case-sensitive disk <c>.ENV</c> is a
/// different file, but it is just as secret.
/// </summary>
public static class SensitiveFiles
{
    /// <summary>
    /// File names that usually hold secrets. Visible in listings, but not readable or writable unless the person
    /// turned on "include sensitive files" for the plugin, or chose the file themselves (document and picked scopes).
    /// </summary>
    public static readonly IReadOnlyList<string> NamePatterns =
    [
        ".env", ".env.*",                       // dotenv files: API keys, database passwords
        "*.pem", "*.key", "*.p12", "*.pfx",     // private keys and certificate bundles
        "id_rsa*", "id_ed25519*",               // SSH keys (also outside .ssh/)
        ".npmrc", ".netrc", ".pypirc",          // registry and FTP/HTTP tokens
        "credentials",                          // ~/.aws/credentials and similar
        "*.keychain*",                          // macOS keychains (login.keychain-db)
    ];

    /// <summary>Directories whose whole content is treated as sensitive, wherever they are.</summary>
    public static readonly IReadOnlyList<string> Directories = [".ssh", ".aws", ".gnupg", ".kube"];

    /// <summary>
    /// The generic credential list: file names that are another application's login wherever they appear. Never
    /// readable or writable, under any scope, even with "include sensitive files" (11 §8.1: the agent vendors' terms do
    /// not allow third parties to collect or relay their subscription credentials).
    /// </summary>
    public static readonly IReadOnlyList<string> CredentialNames =
    [
        ".credentials.json",   // Claude Code: <CLAUDE_CONFIG_DIR>/.credentials.json, whatever the directory is
        "oauth_creds.json",    // Gemini CLI: ~/.gemini/oauth_creds.json
    ];

    /// <summary>
    /// Credential names that count only directly inside an agent's data directory (an <c>agentData</c> scope root or a
    /// directory the host lists in <see cref="PolicyContext.AgentCredentials"/>): <c>auth.json</c> is too common a name
    /// to refuse everywhere. The agent definitions add their own names (<c>signals.credentialFiles</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> AgentCredentialNames = [".credentials.json", "auth.json"];

    /// <summary>Credential files of known agents at their default places, matched as path endings so any home directory counts.</summary>
    public static readonly IReadOnlyList<string> AgentCredentialPaths =
    [
        ".claude/.credentials.json",
        ".codex/auth.json",
        ".grok/auth.json",
        ".local/share/opencode/auth.json",
        ".gemini/oauth_creds.json",
        ".config/github-copilot/hosts.json",
        ".config/github-copilot/apps.json",
    ];

    private static readonly PathGlob[] NameGlobs = [.. NamePatterns.Select(p => new PathGlob(p, ignoreCase: true))];

    /// <summary>A secret-looking file, or anything inside a secret-holding directory (<see cref="Directories"/>).</summary>
    public static bool IsSensitive(string path)
    {
        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;
        foreach (var segment in segments)
        {
            foreach (var directory in Directories)
            {
                if (string.Equals(segment, directory, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        var name = segments[^1];
        foreach (var glob in NameGlobs)
        {
            if (glob.IsMatch(name)) return true;
        }
        return false;
    }

    /// <summary>
    /// Another application's credentials by name alone: <see cref="CredentialNames"/> anywhere, or
    /// <see cref="AgentCredentialPaths"/> at the end of the path. The policy adds the agent-directory rules.
    /// </summary>
    public static bool IsCredentialByName(string path)
    {
        var normalized = path.Replace('\\', '/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        foreach (var credential in CredentialNames)
        {
            if (string.Equals(name, credential, StringComparison.OrdinalIgnoreCase)) return true;
        }
        foreach (var ending in AgentCredentialPaths)
        {
            if (normalized.EndsWith("/" + ending, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Operating-system locations that are never written to, even under an <c>external</c> grant (04 §3.3). Reading
    /// them is a matter of scope like anywhere else.
    /// </summary>
    public static IReadOnlyList<string> DefaultSystemLocations()
    {
        if (!OperatingSystem.IsWindows())
        {
            return
            [
                "/System", "/Library", "/Applications",          // macOS system, system-wide library, apps
                "/usr", "/bin", "/sbin", "/etc", "/private/etc", // /etc is a link to /private/etc on macOS
                "/boot", "/lib", "/lib32", "/lib64", "/libx32",
                "/dev", "/proc", "/sys",
            ];
        }
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var drive = Path.GetPathRoot(windows) is { Length: > 0 } root ? root : @"C:\";
        return
        [
            .. new[]
            {
                windows,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432") ?? "",
                Path.Combine(drive, "Windows"),
                Path.Combine(drive, "Program Files"),
                Path.Combine(drive, "Program Files (x86)"),
            }.Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }
}
