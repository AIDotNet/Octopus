using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Octopus.Platform;
using Octopus.Ssh;

namespace Octopus.App.Services;

/// <summary>
/// The files of SSH hosts for the app: one folder tree per host, the connection they share, and the read-only copies of
/// the files that were opened. Nothing is written to a host.
/// </summary>
/// <param name="controlDirectory">Where the shared connections' sockets go; null: the folder in the home folder.</param>
public sealed class RemoteFiles(IProcessRunner runner, IAppPaths paths, string? controlDirectory = null) : ISharedConnection
{
    /// <summary>The largest file that is copied to this computer.</summary>
    public const long MaxCopyBytes = 5 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, SshFileTree> _trees = new(StringComparer.Ordinal);
    private readonly Lazy<string> _control = new(() => controlDirectory ?? CreateControlDirectory());

    /// <summary>The options that let every run for a host share one connection; the terminal uses them too, so a password is asked once.</summary>
    public IReadOnlyList<string> SharedOptions => SshCommands.SharedConnection(_control.Value);

    /// <summary>The options for riding on the connection of the terminal without becoming a master; empty where that is not possible.</summary>
    public IReadOnlyList<string> ClientOptions => SshCommands.SharedClient(_control.Value);

    /// <summary>The options of the terminal's ssh: the connection is kept alive, so a dropped network ends the terminal's session with a message.</summary>
    public IReadOnlyList<string> TerminalOptions => [.. SshCommands.KeepAlive, .. SharedOptions];

    /// <summary>Where ssh writes its debug log for the terminal of an item (the terminal shows the connection's steps from it).</summary>
    public string ConnectLogPath(string itemId) => Path.Combine(paths.CacheDirectory, "ssh-connect", itemId + ".log");

    /// <summary>
    /// Starts the debug log of a new terminal run: the file is emptied, and the options that make ssh write to it are returned.
    /// </summary>
    public IReadOnlyList<string> StartConnectLog(string itemId)
    {
        var path = ConnectLogPath(itemId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return ["-o", "LogLevel=DEBUG1", "-E", path];
    }

    /// <summary>The folder tree of a host; one per host for the life of the app.</summary>
    public SshFileTree TreeFor(string host) =>
        _trees.GetOrAdd(host, h => new SshFileTree(h, runner, [.. SshCommands.Unattended, .. SharedOptions]));

    /// <summary>
    /// Copies a file of a host into the app's cache and returns the copy's path. The copy is read-only: edits to it are not
    /// written back. Throws <see cref="SshCommandException"/> (or <see cref="SshFileTooLargeException"/>) when it cannot be copied.
    /// </summary>
    public async Task<string> CopyAsync(string host, string remotePath, CancellationToken cancellationToken = default)
    {
        var bytes = await TreeFor(host).ReadAsync(remotePath, MaxCopyBytes, cancellationToken);
        var name = remotePath.Split('/').LastOrDefault(s => s.Length > 0) ?? "file";
        var folder = Path.Combine(paths.CacheDirectory, "remote", Hash(host + "\n" + remotePath));
        Directory.CreateDirectory(folder);
        var local = Path.Combine(folder, name);
        if (File.Exists(local)) MakeWritable(local);
        await File.WriteAllBytesAsync(local, bytes, cancellationToken);
        return local;
    }

    /// <summary>A short folder name for a host and a path: unique enough for the copies of one session's files.</summary>
    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>The sockets are kept in a short folder: a socket path is limited to about 100 characters.</summary>
    private static string CreateControlDirectory()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".octopus", "ssh");
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return folder;
    }

    private static void MakeWritable(string path)
    {
        if (OperatingSystem.IsWindows()) File.SetAttributes(path, FileAttributes.Normal);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
