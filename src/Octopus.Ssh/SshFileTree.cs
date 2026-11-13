using System.Collections.Concurrent;
using Octopus.FileSystem;
using Octopus.Platform;

namespace Octopus.Ssh;

/// <summary>A run on a host that failed; the message is what ssh or the host's shell said, short enough to show.</summary>
public class SshCommandException(string message) : Exception(message);

/// <summary>A file on a host that is larger than the app copies.</summary>
public sealed class SshFileTooLargeException(long size, long limit) : SshCommandException($"{size} bytes is more than the {limit} bytes that are copied.")
{
    public long Size { get; } = size;
    public long Limit { get; } = limit;
}

/// <summary>
/// The file tree of a folder on an SSH host, read with ssh runs. <see cref="List"/> answers from what it has and reads the
/// folder in the background; when a folder's answer changes, the watchers of the tree are told, and the tree reads the
/// folder again. Only read commands are sent: nothing on the host is changed.
/// </summary>
/// <remarks>Folders are read by <c>ls -1Ap</c>, so a name with a line break in it is not shown.</remarks>
public sealed class SshFileTree(string host, IProcessRunner runner, IReadOnlyList<string> options) : IFileTreeService
{
    /// <summary>An answer this recent is not asked for again: the tree asks for the same folder several times in a row.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Folder reads run at once. Opening a folder reads the folders in it ahead: forty of them would start forty ssh runs,
    /// more than a shared connection takes (OpenSSH's MaxSessions is 10), and the rest would fail.
    /// </summary>
    private const int ConcurrentReads = 4;

    private readonly ConcurrentDictionary<string, Listing> _listings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _reading = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _reads = new(ConcurrentReads);
    private readonly List<(string Root, Action<string> OnChanged)> _watchers = [];
    private string? _status;

    /// <summary>The alias of the host, as ssh is given it.</summary>
    public string Host => host;

    /// <summary>The message of the last failed run, or null when the last run worked.</summary>
    public string? Status
    {
        get
        {
            lock (_gate) return _status;
        }
    }

    /// <summary>Raised with the new <see cref="Status"/>, on a background thread, when it changes.</summary>
    public event EventHandler<string?>? StatusChanged;

    private int _running;

    /// <summary>True while an ssh run to the host is under way (connecting, reading a folder, copying a file).</summary>
    public bool IsBusy => Volatile.Read(ref _running) > 0;

    /// <summary>Raised with <see cref="IsBusy"/> when it changes, on a background thread.</summary>
    public event EventHandler<bool>? BusyChanged;

    public IReadOnlyList<FileEntry> List(string directory)
    {
        var known = _listings.TryGetValue(directory, out var listing) ? listing : null;
        if (known is null || DateTimeOffset.UtcNow - known.At > Fresh) StartReading(directory);
        return known?.Entries ?? [];
    }

    /// <summary>Tells <paramref name="onChanged"/> when a folder under <paramref name="root"/> has been read again and may have changed.</summary>
    public IDisposable Watch(string root, Action<string> onChanged)
    {
        var watcher = (root, onChanged);
        lock (_gate) _watchers.Add(watcher);
        return new Stop(() =>
        {
            lock (_gate) _watchers.Remove(watcher);
        });
    }

    /// <summary>Changes made on the host are not seen as they happen: the tree reads a folder when it is asked to.</summary>
    public IDisposable WatchChanges(string root, Action onAnyChange, TimeSpan debounce) => Stop.Nothing;

    /// <summary>A git directory on the host is not watched (git is not read on hosts).</summary>
    public IDisposable WatchGitDirectory(string gitDirectory, string? commonDirectory, Action onChanged, TimeSpan debounce) => Stop.Nothing;

    /// <summary>A file on the host is not watched: a copy is kept by the app instead.</summary>
    public IDisposable WatchFile(string path, Action onChanged, TimeSpan debounce) => Stop.Nothing;

    /// <summary>The absolute path of a folder on the host, with symbolic links resolved. Throws <see cref="SshCommandException"/> when it cannot be entered.</summary>
    public async Task<string> ResolveAsync(string path, CancellationToken cancellationToken = default)
    {
        var output = await RunAsync("cd -- " + SshCommands.RemotePath(path) + " && pwd -P", cancellationToken);
        var resolved = LastLine(output);
        return resolved.StartsWith('/') ? resolved : throw new SshCommandException("The host did not give an absolute folder.");
    }

    /// <summary>The bytes of a file on the host. Throws <see cref="SshFileTooLargeException"/> above <paramref name="maxBytes"/>, and <see cref="SshCommandException"/> when the file cannot be read.</summary>
    public async Task<byte[]> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken = default)
    {
        var quoted = SshCommands.Quote(path);
        var size = long.TryParse(LastLine(await RunAsync("wc -c < " + quoted, cancellationToken)), out var bytes)
            ? bytes
            : throw new SshCommandException("The host did not tell the size of the file.");
        if (size > maxBytes) throw new SshFileTooLargeException(size, maxBytes);

        var encoded = await RunAsync("base64 < " + quoted + " | tr -d '\\n'", cancellationToken);
        try
        {
            return Convert.FromBase64String(encoded.Trim());
        }
        catch (FormatException)
        {
            throw new SshCommandException("The file could not be read from the host.");
        }
    }

    /// <summary>The folder's entries, folders first, each group in the order a file manager shows them. For a folder's text as <c>ls -1Ap</c> printed it.</summary>
    internal static IReadOnlyList<FileEntry> Parse(string directory, string output)
    {
        var entries = new List<FileEntry>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var isDirectory = line.EndsWith('/');
            var name = isDirectory ? line[..^1] : line;
            // The folder's own git data is left out, as the local tree leaves it out.
            if (name.Length == 0 || name is "." or ".." || (isDirectory && name == ".git")) continue;
            entries.Add(new FileEntry(name, Join(directory, name), isDirectory, name.StartsWith('.')));
        }
        return entries
            .OrderBy(e => !e.IsDirectory)
            .ThenBy(e => e.Name, NaturalStringComparer.Instance)
            .ToList();
    }

    /// <summary>Creates an empty file on the host. Throws <see cref="SshCommandException"/> when the name is taken.</summary>
    public Task CreateFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var target = Absolute(path);
        return ChangeAsync(ParentOf(target), $"if [ -e {Path(target)} ]; then echo 'The name is taken.' >&2; exit 1; fi; : > {Path(target)}", cancellationToken);
    }

    /// <summary>Creates a folder on the host. Throws <see cref="SshCommandException"/> when the name is taken.</summary>
    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var target = Absolute(path);
        return ChangeAsync(ParentOf(target), $"mkdir -- {Path(target)}", cancellationToken);
    }

    /// <summary>Renames or moves a file or folder on the host. Throws <see cref="SshCommandException"/> when the new name is taken.</summary>
    public Task RenameAsync(string path, string newPath, CancellationToken cancellationToken = default)
    {
        var source = Absolute(path);
        var target = Absolute(newPath);
        return ChangeAsync(
            ParentOf(source),
            $"if [ -e {Path(target)} ]; then echo 'The name is taken.' >&2; exit 1; fi; mv -- {Path(source)} {Path(target)}",
            cancellationToken);
    }

    /// <summary>
    /// Deletes a file or folder on the host, for good: a host has no trash. The root of the tree and the home folder are
    /// refused.
    /// </summary>
    public Task DeleteAsync(string path, bool isDirectory, CancellationToken cancellationToken = default)
    {
        var target = Absolute(path);
        if (target.Length < 2 || target == "~") throw new ArgumentException("The host's root and home folders are not deleted here.", nameof(path));
        var command = isDirectory ? $"rm -rf -- {Path(target)}" : $"rm -f -- {Path(target)}";
        return ChangeAsync(ParentOf(target), command, cancellationToken);
    }

    /// <summary>
    /// Replaces the content of a file on the host. The bytes go to a temporary file next to it, which then takes its name:
    /// a write that is interrupted leaves the old file as it was, and the file keeps its permissions.
    /// </summary>
    public async Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken = default)
    {
        var target = Absolute(path);
        var quoted = Path(target);
        var temporary = Path(target + ".octopus-save");
        var command =
            $"mode=$(stat -c %a -- {quoted} 2>/dev/null || stat -f %Lp {quoted} 2>/dev/null); " +
            $"cat > {temporary} && {{ [ -z \"$mode\" ] || chmod \"$mode\" {temporary}; }} && mv -f -- {temporary} {quoted} || {{ rm -f -- {temporary}; exit 1; }}";
        await ExecuteAsync(command, bytes, cancellationToken);
        _listings.TryRemove(ParentOf(target), out _);
        Notify(ParentOf(target));
    }

    /// <summary>Whether something exists at the path on the host.</summary>
    public async Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var quoted = Path(Absolute(path));
        return LastLine(await RunAsync($"if [ -e {quoted} ]; then echo yes; else echo no; fi", cancellationToken)) == "yes";
    }

    private async Task ChangeAsync(string directory, string command, CancellationToken cancellationToken)
    {
        await RunAsync(command, cancellationToken);
        // The folder changed: what was read of it is out of date, and the tree is told to read it again.
        _listings.TryRemove(directory, out _);
        Notify(directory);
    }

    private static string Absolute(string path)
    {
        if (!path.StartsWith('/') && path != "~" && !path.StartsWith("~/", StringComparison.Ordinal))
            throw new ArgumentException("A path on the host must be absolute.", nameof(path));
        return path;
    }

    private static string Path(string path) => SshCommands.RemotePath(path);

    private static string ParentOf(string path)
    {
        var index = path.LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }

    private void StartReading(string directory)
    {
        if (!_reading.TryAdd(directory, true)) return;
        _ = ReadFolderAsync(directory);
    }

    private async Task ReadFolderAsync(string directory)
    {
        var previous = _listings.TryGetValue(directory, out var known) ? known.Entries : null;
        IReadOnlyList<FileEntry> entries;
        await _reads.WaitAsync().ConfigureAwait(false);
        try
        {
            entries = Parse(directory, await RunAsync("ls -1Ap -- " + SshCommands.RemotePath(directory), CancellationToken.None));
            _listings[directory] = new Listing(entries, DateTimeOffset.UtcNow);
            SetStatus(null);
        }
        catch (SshCommandException e)
        {
            // What was on screen stays: a failed read must not empty the folder.
            entries = previous ?? [];
            _listings[directory] = new Listing(entries, DateTimeOffset.UtcNow);
            SetStatus(e.Message);
        }
        finally
        {
            _reads.Release();
            _reading.TryRemove(directory, out _);
        }
        // The same answer again (the tree asks often) leaves the tree alone.
        if (previous is null || !previous.SequenceEqual(entries)) Notify(directory);
    }

    private Task<string> RunAsync(string command, CancellationToken cancellationToken) =>
        ExecuteAsync(command, null, cancellationToken);

    private async Task<string> ExecuteAsync(string command, byte[]? input, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _running) == 1) BusyChanged?.Invoke(this, true);
        ProcessResult result;
        try
        {
            var arguments = SshCommands.Arguments(host, command, options);
            result = input is null
                ? await runner.RunAsync("ssh", arguments, timeout: RunTimeout, cancellationToken: cancellationToken)
                : await runner.RunWithInputAsync("ssh", arguments, input, timeout: RunTimeout, cancellationToken: cancellationToken);
        }
        finally
        {
            if (Interlocked.Decrement(ref _running) == 0) BusyChanged?.Invoke(this, false);
        }
        if (result.TimedOut) throw new SshCommandException("The host did not answer in time.");
        if (result.ExitCode != 0)
        {
            var message = LastLine(result.StandardError);
            throw new SshCommandException(message.Length > 0 ? message : $"ssh ended with code {result.ExitCode}.");
        }
        return result.StandardOutput;
    }

    private void Notify(string directory)
    {
        (string Root, Action<string> OnChanged)[] targets;
        lock (_gate) targets = _watchers.Where(w => Within(w.Root, directory)).ToArray();
        foreach (var (_, onChanged) in targets) onChanged(directory);
    }

    private void SetStatus(string? message)
    {
        bool changed;
        lock (_gate)
        {
            changed = _status != message;
            _status = message;
        }
        if (changed) StatusChanged?.Invoke(this, message);
    }

    private static bool Within(string root, string directory) =>
        directory == root || directory.StartsWith(root.EndsWith('/') ? root : root + "/", StringComparison.Ordinal);

    private static string Join(string directory, string name) =>
        directory.EndsWith('/') ? directory + name : directory + "/" + name;

    /// <summary>The last line with text: ssh may print warnings first and the reason last.</summary>
    private static string LastLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";

    private sealed record Listing(IReadOnlyList<FileEntry> Entries, DateTimeOffset At);

    private sealed class Stop(Action action) : IDisposable
    {
        public static readonly IDisposable Nothing = new Stop(() => { });

        public void Dispose() => action();
    }
}
