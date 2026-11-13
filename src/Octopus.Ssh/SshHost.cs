namespace Octopus.Ssh;

/// <summary>
/// A host alias from ~/.ssh/config, with the options Octopus shows. Connecting is left to the ssh program, which reads
/// the same file, so these values are for display only and are never used to open a connection.
/// </summary>
/// <param name="Alias">The name after <c>Host</c>: what a project refers to.</param>
/// <param name="HostName">The address ssh connects to; the alias when the file sets none.</param>
/// <param name="User">The login name, or null for the local user name.</param>
/// <param name="Port">The port, or null for 22 (or whatever the file's defaults say).</param>
/// <param name="IdentityFile">The first key file as written in the file (not expanded), or null.</param>
/// <param name="ProxyJump">The jump host as written in the file, or null.</param>
/// <param name="Protocol">How the terminal of the host connects; <see cref="SshHostProtocol.Ssh"/> unless the host's block says otherwise.</param>
/// <param name="Editable">True when the host has a block of its own in ~/.ssh/config (not in an included file, not shared with other aliases): only then can the app change or remove it.</param>
public sealed record SshHost(
    string Alias,
    string HostName,
    string? User,
    int? Port,
    string? IdentityFile,
    string? ProxyJump,
    SshHostProtocol Protocol = SshHostProtocol.Ssh,
    bool Editable = false)
{
    /// <summary>The draft that has this host's fields, for editing.</summary>
    public SshHostDraft ToDraft() => new(
        Alias,
        HostName == Alias ? null : HostName,
        User,
        Port?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        IdentityFile,
        ProxyJump,
        Protocol);
}

/// <summary>
/// How a host's terminal connects. The folder tree and the tunnels always use ssh (Mosh starts on it), so they do not
/// exist for <see cref="Telnet"/> hosts. The choice is kept in the host's block of ~/.ssh/config as a comment
/// (<c># octopus: protocol=mosh</c>), which ssh ignores.
/// </summary>
public enum SshHostProtocol
{
    /// <summary>The ssh program.</summary>
    Ssh = 0,
    /// <summary>The mosh program: ssh to start with, then a connection that survives roaming and sleep.</summary>
    Mosh = 1,
    /// <summary>The telnet program, to the host name and the port (23 unless set). Plain text: only for networks you trust.</summary>
    Telnet = 2,
}

/// <summary>What the last reading of ~/.ssh/config found.</summary>
/// <param name="ConfigPath">The file that was read (whether or not it exists).</param>
/// <param name="ConfigExists">False when there is no such file: then there are no hosts.</param>
/// <param name="ScannedAt">When the file was read; default when it never was.</param>
/// <param name="Hosts">The concrete host aliases, in the order the file gives them.</param>
public sealed record SshHostScan(string ConfigPath, bool ConfigExists, DateTimeOffset ScannedAt, IReadOnlyList<SshHost> Hosts);
