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
public sealed record SshHost(string Alias, string HostName, string? User, int? Port, string? IdentityFile, string? ProxyJump);

/// <summary>What the last reading of ~/.ssh/config found.</summary>
/// <param name="ConfigPath">The file that was read (whether or not it exists).</param>
/// <param name="ConfigExists">False when there is no such file: then there are no hosts.</param>
/// <param name="ScannedAt">When the file was read; default when it never was.</param>
/// <param name="Hosts">The concrete host aliases, in the order the file gives them.</param>
public sealed record SshHostScan(string ConfigPath, bool ConfigExists, DateTimeOffset ScannedAt, IReadOnlyList<SshHost> Hosts);
