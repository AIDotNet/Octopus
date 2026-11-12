namespace Octopus.App.Services;

/// <summary>
/// The connection that the terminals and the folder tree of a host share (OpenSSH's ControlMaster, see
/// <c>RemoteFiles.SharedOptions</c>). A tunnel to a host that needs a password or a second factor rides on it: it cannot
/// sign in by itself, the terminal did.
/// </summary>
public interface ISharedConnection
{
    /// <summary>
    /// The options that make a run use the shared connection and never start one; empty where there is no such thing
    /// (Windows' OpenSSH has no control sockets).
    /// </summary>
    IReadOnlyList<string> ClientOptions { get; }
}
