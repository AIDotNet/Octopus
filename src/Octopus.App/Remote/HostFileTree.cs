using System.Collections.Concurrent;
using Octopus.Core.Projects;
using Octopus.FileSystem;
using Octopus.Git;
using Octopus.Remote.Client;
using Octopus.Remote.Protocol;

namespace Octopus.App.Remote;

/// <summary>
/// The folders of a host's projects, as the right panel's tree reads them (design/12-远程操作.md §6.2): every listing,
/// read and write is the host's doing. The host says when something under a watched folder changed; the tree is then
/// read again.
/// </summary>
public sealed class HostFileTree : IRemoteFileTree
{
    /// <summary>An answer this recent is not asked for again: the tree asks for the same folder several times in a row.</summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(20);

    private readonly RemoteClientRuntime _runtime;
    private readonly string _hostId;
    private readonly ConcurrentDictionary<string, (long At, IReadOnlyList<FileEntry> Entries)> _listed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<FileEntry>>> _reading = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    // Told when the host says something under the folder changed (source control reads again).
    private readonly List<(string Root, Action Changed)> _watchers = [];
    // Told when a folder under the root was read again and is not what it was (the tree shows it again).
    private readonly List<(string Root, Action<string> Changed)> _treeWatchers = [];
    private RemoteHostClient? _listening;
    private string? _status;
    private int _running;

    public HostFileTree(RemoteClientRuntime runtime, string hostId)
    {
        _runtime = runtime;
        _hostId = hostId;
        // After a reconnect the host has forgotten what this device watched.
        runtime.StateChanged += id =>
        {
            if (id == hostId && runtime.StateOf(hostId) == RemoteHostState.Connected) _ = RewatchAsync();
        };
    }

    public string? Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            StatusChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<string?>? StatusChanged;
    public bool IsBusy => Volatile.Read(ref _running) > 0;
    public event EventHandler<bool>? BusyChanged;

    /// <summary>
    /// What is known of the folder, at once (the tree calls this on the UI thread): nothing the first time. When that is
    /// missing or no longer fresh the host is asked in the background, and whoever watches the folder (<see cref="Watch"/>)
    /// is told once the answer is there and differs.
    /// </summary>
    public IReadOnlyList<FileEntry> List(string directory)
    {
        var known = _listed.TryGetValue(directory, out var listing);
        if (!known || Environment.TickCount64 - listing.At > Fresh.TotalMilliseconds) _ = ReadFolderAsync(directory);
        return known ? listing.Entries : [];
    }

    /// <summary>Asks the host for the folder now and returns its answer; a folder that cannot be read is empty, and <see cref="Status"/> says why.</summary>
    public Task<IReadOnlyList<FileEntry>> ReadFolderAsync(string directory) =>
        _reading.GetOrAdd(directory, d => Task.Run(() => ReadFolderCoreAsync(d)));

    private async Task<IReadOnlyList<FileEntry>> ReadFolderCoreAsync(string directory)
    {
        var previous = _listed.TryGetValue(directory, out var known) ? known.Entries : null;
        IReadOnlyList<FileEntry> entries;
        try
        {
            entries = await Run(async client =>
                (IReadOnlyList<FileEntry>)[.. (await client.ListFolderAsync(directory).ConfigureAwait(false)).Select(e => new FileEntry(e.Name, e.Path, e.IsDirectory, e.IsHidden))]).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // What was on screen stays: a failed read must not empty the folder.
            entries = previous ?? [];
        }
        finally
        {
            _reading.TryRemove(directory, out _);
        }
        _listed[directory] = (Environment.TickCount64, entries);
        if (previous is null || !previous.SequenceEqual(entries)) NotifyTree(directory);
        return entries;
    }

    /// <summary>Tells <paramref name="onChanged"/> when a folder under <paramref name="root"/> has been read again and is not what it was.</summary>
    public IDisposable Watch(string root, Action<string> onChanged)
    {
        var watcher = (root, onChanged);
        lock (_gate) _treeWatchers.Add(watcher);
        _ = WatchOnHostAsync(root);
        return new Removal(this, root, () =>
        {
            lock (_gate) _treeWatchers.Remove(watcher);
        });
    }

    public IDisposable WatchChanges(string root, Action onAnyChange, TimeSpan debounce)
    {
        var watcher = (root, onAnyChange);
        lock (_gate) _watchers.Add(watcher);
        _ = WatchOnHostAsync(root);
        return new Removal(this, root, () =>
        {
            lock (_gate) _watchers.Remove(watcher);
        });
    }

    private void NotifyTree(string directory)
    {
        List<Action<string>> callbacks;
        lock (_gate) callbacks = [.. _treeWatchers.Where(w => IsUnder(w.Root, directory)).Select(w => w.Changed)];
        foreach (var callback in callbacks) callback(directory);
    }

    // The host's separators are the host's own: either kind ends a folder name.
    private static bool IsUnder(string root, string path) =>
        path == root || (path.StartsWith(root, StringComparison.Ordinal) && path.Length > root.Length && (path[root.Length] is '/' or '\\' || root.EndsWith('/') || root.EndsWith('\\')));

    // The host's git folders are inside the watched project folder: its changes arrive with the folder's.
    public IDisposable WatchGitDirectory(string gitDirectory, string? commonDirectory, Action onChanged, TimeSpan debounce) => Nothing.Instance;

    public IDisposable WatchFile(string path, Action onChanged, TimeSpan debounce) => Nothing.Instance;

    public Task<string> ResolveAsync(string path, CancellationToken cancellationToken = default) => Run(client => client.ResolveAsync(path, cancellationToken));

    public Task<byte[]> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken = default) => Run(async client =>
    {
        try { return await client.ReadFileAsync(path, maxBytes, cancellationToken).ConfigureAwait(false); }
        catch (InvalidDataException e) { throw new RemoteFileTooLargeException(e.Data["size"] is long size ? size : maxBytes + 1, maxBytes); }
    });

    public Task WriteAsync(string path, byte[] bytes, CancellationToken cancellationToken = default) => Run(async client =>
    {
        await client.WriteFileAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        return true;
    });

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) => Run(client => client.ExistsAsync(path, cancellationToken));

    public Task CreateFileAsync(string path, CancellationToken cancellationToken = default) => Change(client => client.CreateFileAsync(path, cancellationToken));

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) => Change(client => client.CreateDirectoryAsync(path, cancellationToken));

    public Task RenameAsync(string path, string newPath, CancellationToken cancellationToken = default) => Change(client => client.RenameAsync(path, newPath, cancellationToken));

    public Task DeleteAsync(string path, bool isDirectory, CancellationToken cancellationToken = default) => Change(client => client.DeleteAsync(path, isDirectory, cancellationToken));

    private Task Change(Func<RemoteHostClient, Task> action) => Run(async client =>
    {
        await action(client).ConfigureAwait(false);
        Reread();
        return true;
    });

    // Everything read so far may be out of date: each folder is read again, and the tree told where it differs.
    private void Reread()
    {
        foreach (var directory in _listed.Keys) _ = ReadFolderAsync(directory);
    }

    // One request to the host: counted as busy, its failure kept as the status, and always an IOException for the caller.
    private async Task<T> Run<T>(Func<RemoteHostClient, Task<T>> action)
    {
        if (Interlocked.Increment(ref _running) == 1) BusyChanged?.Invoke(this, true);
        try
        {
            var client = await _runtime.ConnectAsync(_hostId).WaitAsync(CallLimit).ConfigureAwait(false);
            Listen(client);
            var result = await action(client).WaitAsync(CallLimit).ConfigureAwait(false);
            Status = null;
            return result;
        }
        catch (Exception e) when (e is RemoteException or TimeoutException or ObjectDisposedException || (e is IOException && e is not RemoteFileTooLargeException))
        {
            Status = e is TimeoutException ? "The host did not answer in time." : e.Message;
            throw e as IOException ?? new IOException(Status, e);
        }
        finally
        {
            if (Interlocked.Decrement(ref _running) == 0) BusyChanged?.Invoke(this, false);
        }
    }

    private async Task WatchOnHostAsync(string root)
    {
        try
        {
            var client = await _runtime.ConnectAsync(_hostId).ConfigureAwait(false);
            Listen(client);
            await client.WatchFolderAsync(root).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or RemoteException or ObjectDisposedException) { /* asked again when the host is back */ }
    }

    private async Task RewatchAsync()
    {
        List<string> roots;
        lock (_gate) roots = [.. _watchers.Select(w => w.Root).Concat(_treeWatchers.Select(w => w.Root)).Distinct(StringComparer.Ordinal)];
        foreach (var root in roots) await WatchOnHostAsync(root).ConfigureAwait(false);
        // What changed while the host was away was never reported: everything watched is read again.
        foreach (var root in roots) OnFolderChanged(root);
    }

    private void Listen(RemoteHostClient client)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_listening, client)) return;
            _listening = client;
        }
        client.FolderChanged += OnFolderChanged;
    }

    // The host says something under the folder changed: what was read of it is read again, and source control is told.
    private void OnFolderChanged(string folder)
    {
        foreach (var directory in _listed.Keys.Where(d => IsUnder(folder, d))) _ = ReadFolderAsync(directory);
        List<Action> callbacks;
        lock (_gate) callbacks = [.. _watchers.Where(w => w.Root == folder).Select(w => w.Changed)];
        foreach (var callback in callbacks) callback();
    }

    private sealed class Removal(HostFileTree tree, string root, Action remove) : IDisposable
    {
        public void Dispose()
        {
            remove();
            bool last;
            lock (tree._gate) last = tree._watchers.All(w => w.Root != root) && tree._treeWatchers.All(w => w.Root != root);
            if (last && tree._runtime.ClientOf(tree._hostId) is { } client)
            {
                _ = client.UnwatchFolderAsync(root).ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            }
        }
    }

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// For a project that is not on this computer, what reads its files and runs its git: an SSH host's tree (no git), or a
/// host that runs Octopus (both). The one place that knows which.
/// </summary>
public sealed class RemoteProjectAccess(Services.RemoteFiles ssh, RemoteClientRuntime runtime)
{
    private readonly ConcurrentDictionary<string, HostFileTree> _trees = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RemoteGitService> _git = new(StringComparer.Ordinal);

    /// <summary>The tree of the machine the project is on; null for a project of this computer.</summary>
    public IRemoteFileTree? TreeOf(Project? project) =>
        project is null ? null
        : project.RemoteHost is { } host ? TreeOfHost(host)
        : project.SshHost is { } alias ? ssh.TreeFor(alias)
        : null;

    /// <summary>The git service of the project's host; null where git runs on this computer or not at all (an SSH host).</summary>
    public IGitService? GitOf(Project? project) => project?.RemoteHost is { } host ? GitOfHost(host) : null;

    /// <summary>The one tree of a host that runs Octopus: what it has read and what it watches is shared by all who ask.</summary>
    public HostFileTree TreeOfHost(string hostId) => _trees.GetOrAdd(hostId, id => new HostFileTree(runtime, id));

    public RemoteGitService GitOfHost(string hostId) => _git.GetOrAdd(hostId, id => new RemoteGitService(runtime, id));

    /// <summary>What names the machine in the copies' cache folder: the ssh alias, or the host's device id.</summary>
    public static string? MachineOf(Project? project) => project?.RemoteHost ?? project?.SshHost;
}
