using Octopus.FileSystem;
using Octopus.Platform;

namespace Octopus.Ssh;

/// <summary>
/// Browses folders on an SSH host using single-shot POSIX commands over the shared SSH connection.
/// Returns only directories, naturally sorted, with <c>~</c> and symlinks resolved via <c>pwd -P</c>.
/// </summary>
public sealed class SshFolderBrowser(string host, IProcessRunner runner, IReadOnlyList<string> options, string? displayName = null) : IFolderBrowser
{
    private const string Marker = "---OCTOPUS-BROWSE---";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(20);
    private string? _cachedHome;

    public string HostDisplayName => string.IsNullOrWhiteSpace(displayName) ? host : displayName;
    public bool CanCreateFolder => true;

    public async Task<FolderBrowsePage> BrowseAsync(string? path = null, bool listRoots = false, bool fallbackToParent = false, CancellationToken cancellationToken = default)
    {
        var requested = listRoots ? "/" : string.IsNullOrWhiteSpace(path) ? "~" : path.Trim();
        var command = BuildBrowseCommand(requested, fallbackToParent);
        var output = await RunAsync(command, cancellationToken).ConfigureAwait(false);
        return ParseBrowseOutput(output, requested == "~");
    }

    public async Task<string> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken = default)
    {
        var cleanName = FolderBrowserPaths.ValidateFolderName(name);
        var trimmedParent = (parentPath ?? "").Trim();
        if (trimmedParent.Length == 0 || (!trimmedParent.StartsWith('/') && trimmedParent != "~" && !trimmedParent.StartsWith("~/", StringComparison.Ordinal)))
        {
            throw new IOException("A folder path on the SSH host must be absolute.");
        }

        var target = Join(trimmedParent, cleanName);
        var quoted = SshCommands.RemotePath(target);
        var command = $"if [ -e {quoted} ]; then echo 'A folder or file with that name already exists.' >&2; exit 1; fi; mkdir -- {quoted}";
        await RunAsync(command, cancellationToken).ConfigureAwait(false);
        return target;
    }

    internal static string BuildBrowseCommand(string requested, bool fallbackToParent)
    {
        var remote = SshCommands.RemotePath(requested);
        if (!fallbackToParent)
        {
            return $"cd -- {remote} && pwd -P && printf '\\n{Marker}\\n' && ls -1Ap -- .";
        }

        // Walk up to the nearest existing ancestor; if nothing matches, fall back to $HOME.
        return $"p={remote}; " +
               "while [ ! -d \"$p\" ]; do " +
               "parent=$(dirname -- \"$p\"); " +
               "if [ \"$parent\" = \"$p\" ]; then p=\"$HOME\"; break; fi; " +
               "p=\"$parent\"; " +
               "done; " +
               $"cd -- \"$p\" && pwd -P && printf '\\n{Marker}\\n' && ls -1Ap -- .";
    }

    internal FolderBrowsePage ParseBrowseOutput(string output, bool wasHome)
    {
        var normalized = output.Replace("\r\n", "\n", StringComparison.Ordinal);
        var markerIndex = normalized.IndexOf(Marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new IOException("The SSH host did not return a readable directory listing.");
        }

        var header = normalized[..markerIndex];
        var current = header.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        if (!current.StartsWith('/'))
        {
            throw new IOException("The SSH host did not return an absolute folder path.");
        }

        if (wasHome) _cachedHome = current;
        var home = _cachedHome ?? "~";

        var body = normalized[(markerIndex + Marker.Length)..];
        var entries = new List<FolderBrowseEntry>();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.EndsWith('/')) continue;
            var name = line[..^1];
            if (name.Length == 0 || name is "." or "..") continue;
            entries.Add(new FolderBrowseEntry(name, Join(current, name), IsHidden: name.StartsWith('.')));
        }

        entries.Sort(static (a, b) => NaturalStringComparer.Instance.Compare(a.Name, b.Name));

        var parent = ParentOf(current);
        FolderBrowseEntry[] roots = [new("/", "/", IsHidden: false, Kind: FolderEntryKind.Drive)];
        return new FolderBrowsePage(current, parent, "/", home, entries, roots);
    }

    private async Task<string> RunAsync(string command, CancellationToken cancellationToken)
    {
        var arguments = SshCommands.Arguments(host, command, options);
        var result = await runner.RunAsync("ssh", arguments, timeout: RunTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.TimedOut) throw new IOException("The SSH host did not answer in time.");
        if (result.ExitCode != 0)
        {
            var message = LastLine(result.StandardError);
            throw new IOException(message.Length > 0 ? message : $"ssh ended with code {result.ExitCode}.");
        }
        return result.StandardOutput;
    }

    private static string? ParentOf(string path)
    {
        if (path == "/") return null;
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    private static string Join(string directory, string name) =>
        directory == "/" ? "/" + name : directory.TrimEnd('/') + "/" + name;

    private static string LastLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
}
