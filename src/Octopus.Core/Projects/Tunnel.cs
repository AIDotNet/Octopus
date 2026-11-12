namespace Octopus.Core.Projects;

/// <summary>Which way a tunnel carries connections (design/11-SSH隧道.md §1).</summary>
public enum TunnelKind
{
    /// <summary>Connections to a port on this computer go to an address that the SSH host can reach (<c>ssh -L</c>).</summary>
    Local,
    /// <summary>Connections to a port on the SSH host go to an address that this computer can reach (<c>ssh -R</c>).</summary>
    Remote,
}

/// <summary>
/// One port forward of a project on an SSH host. Kept in <see cref="Project.Tunnels"/>; whether it is running is not
/// saved. What the fields stand for depends on <see cref="Kind"/>:
/// <list type="table">
/// <item><term>Local</term><description><see cref="BindAddress"/> and <see cref="ListenPort"/> are on this computer; the target is relative to the SSH host.</description></item>
/// <item><term>Remote</term><description><see cref="BindAddress"/> and <see cref="ListenPort"/> are on the SSH host; the target is relative to this computer.</description></item>
/// </list>
/// </summary>
public sealed class TunnelSpec
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>The name the person gave; null or empty shows a name made from the ports.</summary>
    public string? Name { get; set; }
    public TunnelKind Kind { get; set; }
    /// <summary>The address the listening side binds to. The loopback address by default: nobody else on the network can use it.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; }
    public string TargetHost { get; set; } = "localhost";
    public int TargetPort { get; set; }
    /// <summary>Start it the first time its project is opened in a run of the app.</summary>
    public bool AutoStart { get; set; }
    /// <summary>Connect again, with growing pauses, when the connection drops.</summary>
    public bool AutoReconnect { get; set; } = true;

    public TunnelSpec Clone() => (TunnelSpec)MemberwiseClone();
}
