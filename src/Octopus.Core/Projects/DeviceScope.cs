namespace Octopus.Core.Projects;

/// <summary>
/// Custom visual appearance of a device badge on the rail: the same color, icon (Lucide or built-in pixel icon) or
/// cropped picture thumbnail choices offered for project squares.
/// </summary>
public sealed class DeviceAppearance
{
    /// <summary>Fill color (e.g. <c>#6366F1</c>) behind the initial or icon; null uses the device's default color.</summary>
    public string? Color { get; set; }

    /// <summary>Lucide icon name or built-in pixel icon (<c>pixel:…</c>); null shows the initial (or default when uncustomized).</summary>
    public string? Icon { get; set; }

    /// <summary>Thumbnail file name in <c>project-icons</c>; null for none.</summary>
    public string? Image { get; set; }
}

/// <summary>
/// Canonical string keys identifying which device a project belongs to:
/// <c>local</c> for this computer, <c>remote:&lt;hostId&gt;</c> for a paired Octopus host,
/// and <c>ssh:&lt;alias&gt;</c> for an SSH host.
/// </summary>
public static class DeviceKeys
{
    public const string Local = "local";
    public const string RemotePrefix = "remote:";
    public const string SshPrefix = "ssh:";

    public static string Remote(string hostId) => RemotePrefix + hostId.Trim();

    public static string Ssh(string alias) => SshPrefix + alias.Trim();

    public static string Of(Project project) =>
        project.RemoteHost is { Length: > 0 } remote ? Remote(remote)
        : project.SshHost is { Length: > 0 } ssh ? Ssh(ssh)
        : Local;

    public static string Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return Local;
        }
        var trimmed = key.Trim();
        if (trimmed.Equals(Local, StringComparison.OrdinalIgnoreCase))
        {
            return Local;
        }
        if (trimmed.StartsWith(RemotePrefix, StringComparison.Ordinal) && trimmed.Length > RemotePrefix.Length)
        {
            var id = trimmed[RemotePrefix.Length..].Trim();
            return id.Length > 0 ? RemotePrefix + id : Local;
        }
        if (trimmed.StartsWith(SshPrefix, StringComparison.Ordinal) && trimmed.Length > SshPrefix.Length)
        {
            var alias = trimmed[SshPrefix.Length..].Trim();
            return alias.Length > 0 ? SshPrefix + alias : Local;
        }
        return Local;
    }

    public static (ProjectLocationKind Kind, string? Target) Parse(string? key)
    {
        var normalized = Normalize(key);
        if (normalized.StartsWith(RemotePrefix, StringComparison.Ordinal))
        {
            return (ProjectLocationKind.Remote, normalized[RemotePrefix.Length..]);
        }
        if (normalized.StartsWith(SshPrefix, StringComparison.Ordinal))
        {
            return (ProjectLocationKind.Ssh, normalized[SshPrefix.Length..]);
        }
        return (ProjectLocationKind.Local, null);
    }

    public static bool Matches(Project project, string? deviceKey) =>
        string.Equals(Of(project), Normalize(deviceKey), StringComparison.Ordinal);
}
