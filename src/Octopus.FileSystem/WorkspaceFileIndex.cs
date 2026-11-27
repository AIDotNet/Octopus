using System.IO.Enumeration;

namespace Octopus.FileSystem;

/// <summary>
/// High-performance, filesystem-watched workspace file index for instant <c>@</c> fuzzy path search.
/// Uses a single-pass <see cref="FileSystemEnumerable{TResult}"/> two-queue breadth-first walk (indexing
/// non-ignored source files across the entire tree first, then bounded deferred ignored entries),
/// keeps entries fresh via <see cref="TreeWatcher"/>, and serves queries synchronously from an immutable snapshot.
/// </summary>
public sealed class WorkspaceFileIndex : IWorkspaceFileIndex
{
    internal const int MaxIndexedFiles = 50_000;
    internal const int MaxDeferredIgnoredFiles = 4_000;
    internal const int MaxCachedWorkspaces = 12;

    private static readonly TimeSpan FallbackRefreshInterval = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> HardSkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg",
        "node_modules", "bower_components",
        "bin", "obj", ".vs", ".idea",
        "dist", "dist_electron", "build", "target", "coverage",
        ".next", ".nuxt", ".turbo", ".parcel-cache", ".svelte-kit", ".angular",
        "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", ".eggs",
        ".venv", "venv",
        ".gradle", ".cache",
    };

    private static readonly EnumerationOptions WalkOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
    };

    /// <summary>Shared fallback instance when callers do not resolve <see cref="IWorkspaceFileIndex"/> from DI.</summary>
    public static WorkspaceFileIndex Shared { get; } = new();

    private readonly Lock _cacheGate = new();
    private readonly Dictionary<string, WorkspaceSlot> _slots = new(
        PathGlob.PlatformIgnoresCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly int _maxIndexedFiles;
    private readonly int _maxDeferredIgnoredFiles;
    private bool _disposed;

    public WorkspaceFileIndex()
        : this(MaxIndexedFiles, MaxDeferredIgnoredFiles)
    {
    }

    internal WorkspaceFileIndex(int maxIndexedFiles, int maxDeferredIgnoredFiles)
    {
        _maxIndexedFiles = Math.Max(1, maxIndexedFiles);
        _maxDeferredIgnoredFiles = Math.Max(0, maxDeferredIgnoredFiles);
    }

    public IReadOnlyList<WorkspaceFileMatch> Search(
        string root,
        string query,
        int maxResults = 20,
        Action? onRefreshed = null)
    {
        if (string.IsNullOrWhiteSpace(root) || maxResults <= 0) return [];
        var normalized = NormalizeRoot(root);
        if (normalized is null) return [];

        var slot = GetOrCreateSlot(normalized);
        if (slot is null) return [];

        var snapshot = slot.Snapshot;
        if (snapshot is null || slot.NeedsRefresh(DateTimeOffset.UtcNow))
        {
            slot.EnsureRefreshStarted(onRefreshed);
        }

        if (snapshot is null) return [];
        return FuzzyPathMatcher.Search(snapshot.Entries, snapshot.Root, query, maxResults);
    }

    public Task WarmAsync(string root, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root)) return Task.CompletedTask;
        var normalized = NormalizeRoot(root);
        if (normalized is null) return Task.CompletedTask;

        var slot = GetOrCreateSlot(normalized);
        if (slot is null) return Task.CompletedTask;

        var task = slot.EnsureRefreshStarted(onRefreshed: null);
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    public void Invalidate(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        var normalized = NormalizeRoot(root);
        if (normalized is null) return;

        WorkspaceSlot? slot;
        lock (_cacheGate)
        {
            _slots.TryGetValue(normalized, out slot);
        }
        slot?.MarkDirty();
    }

    public void Dispose()
    {
        List<WorkspaceSlot> toDispose;
        lock (_cacheGate)
        {
            if (_disposed) return;
            _disposed = true;
            toDispose = [.. _slots.Values];
            _slots.Clear();
        }
        foreach (var slot in toDispose)
        {
            slot.Dispose();
        }
    }

    private WorkspaceSlot? GetOrCreateSlot(string normalizedRoot)
    {
        WorkspaceSlot? evicted = null;
        WorkspaceSlot slot;
        lock (_cacheGate)
        {
            if (_disposed) return null;
            if (_slots.TryGetValue(normalizedRoot, out var existing))
            {
                existing.Touch();
                return existing;
            }

            if (_slots.Count >= MaxCachedWorkspaces)
            {
                string? oldestKey = null;
                var oldestTicks = long.MaxValue;
                foreach (var kv in _slots)
                {
                    var ticks = kv.Value.LastAccessedTicks;
                    if (ticks < oldestTicks)
                    {
                        oldestTicks = ticks;
                        oldestKey = kv.Key;
                    }
                }
                if (oldestKey is not null && _slots.Remove(oldestKey, out evicted))
                {
                    // Disposed outside the lock below.
                }
            }

            slot = new WorkspaceSlot(normalizedRoot, _maxIndexedFiles, _maxDeferredIgnoredFiles);
            _slots[normalizedRoot] = slot;
        }

        evicted?.Dispose();
        return slot;
    }

    private static string? NormalizeRoot(string root)
    {
        try
        {
            var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.Length == 0 ? Path.DirectorySeparatorChar.ToString() : full;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Walks <paramref name="root"/> in breadth-first order with a two-queue priority strategy:
    /// non-gitignored files across the entire tree are indexed first, followed by a bounded budget
    /// of gitignored files so build/log folders never starve deep source files.
    /// </summary>
    internal static IndexedFileEntry[] ScanWorkspace(string root, int maxIndexedFiles, int maxDeferredIgnoredFiles)
    {
        if (!Directory.Exists(root)) return [];

        var ignore = GitIgnoreMatcher.Load(root);
        var entries = new List<IndexedFileEntry>(512);
        var primaryQueue = new Queue<(string FullPath, string RelativePath)>();
        var deferredDirs = new Queue<(string FullPath, string RelativePath)>();
        var deferredFiles = new List<string>();

        primaryQueue.Enqueue((root, ""));

        // Phase 1: BFS over all non-ignored directories and files across the entire repository.
        while (primaryQueue.Count > 0 && entries.Count < maxIndexedFiles)
        {
            var (dirFull, dirRel) = primaryQueue.Dequeue();
            EnumerateDirectory(
                dirFull,
                dirRel,
                inIgnoredSubtree: false,
                ignore,
                entries,
                maxIndexedFiles,
                primaryQueue,
                deferredDirs,
                deferredFiles,
                maxDeferredIgnoredFiles);
        }

        // Phase 2: If capacity remains, include a bounded number of gitignored files (e.g. .env, output/report.json)
        // marked as ignored so source files naturally win on tie-breaks.
        var deferredBudget = Math.Min(maxDeferredIgnoredFiles, maxIndexedFiles - entries.Count);
        if (deferredBudget > 0)
        {
            var deferredAdded = 0;
            for (var i = 0; i < deferredFiles.Count && deferredAdded < deferredBudget; i++)
            {
                entries.Add(new IndexedFileEntry(deferredFiles[i], isIgnored: true));
                deferredAdded++;
            }

            var deferredCap = entries.Count + (deferredBudget - deferredAdded);
            while (deferredDirs.Count > 0 && entries.Count < deferredCap)
            {
                var (dirFull, dirRel) = deferredDirs.Dequeue();
                EnumerateDirectory(
                    dirFull,
                    dirRel,
                    inIgnoredSubtree: true,
                    ignore,
                    entries,
                    deferredCap,
                    deferredDirs,
                    deferredDirs,
                    deferredFiles: null,
                    maxDeferredIgnoredFiles: 0);
            }
        }

        return [.. entries];
    }

    private static void EnumerateDirectory(
        string dirFull,
        string dirRel,
        bool inIgnoredSubtree,
        GitIgnoreMatcher ignore,
        List<IndexedFileEntry> entries,
        int limit,
        Queue<(string FullPath, string RelativePath)> dirQueue,
        Queue<(string FullPath, string RelativePath)> deferredDirQueue,
        List<string>? deferredFiles,
        int maxDeferredIgnoredFiles)
    {
        FileSystemEnumerable<(string Name, string FullPath, bool IsDirectory, bool IsReparsePoint)> children;
        try
        {
            children = new FileSystemEnumerable<(string, string, bool, bool)>(
                dirFull,
                static (ref FileSystemEntry e) => (
                    e.FileName.ToString(),
                    e.ToFullPath(),
                    e.IsDirectory,
                    (e.Attributes & FileAttributes.ReparsePoint) != 0),
                WalkOptions);
        }
        catch (Exception)
        {
            return;
        }

        using var enumerator = children.GetEnumerator();
        while (entries.Count < limit)
        {
            (string Name, string FullPath, bool IsDirectory, bool IsReparsePoint) item;
            try
            {
                if (!enumerator.MoveNext()) break;
                item = enumerator.Current;
            }
            catch (Exception)
            {
                break;
            }

            if (item.IsDirectory)
            {
                if (item.IsReparsePoint || HardSkippedDirectories.Contains(item.Name)) continue;

                var childRel = dirRel.Length == 0 ? item.Name : $"{dirRel}/{item.Name}";
                if (!inIgnoredSubtree && ignore.IsIgnored(childRel, item.Name, isDirectory: true))
                {
                    if (maxDeferredIgnoredFiles > 0)
                    {
                        deferredDirQueue.Enqueue((item.FullPath, childRel));
                    }
                    continue;
                }

                dirQueue.Enqueue((item.FullPath, childRel));
                continue;
            }

            var fileRel = dirRel.Length == 0 ? item.Name : $"{dirRel}/{item.Name}";
            if (!inIgnoredSubtree && ignore.IsIgnored(fileRel, item.Name, isDirectory: false))
            {
                if (deferredFiles is not null && deferredFiles.Count < maxDeferredIgnoredFiles)
                {
                    deferredFiles.Add(fileRel);
                }
                continue;
            }

            entries.Add(new IndexedFileEntry(fileRel, isIgnored: inIgnoredSubtree));
        }
    }

    private sealed record WorkspaceSnapshot(string Root, IndexedFileEntry[] Entries, DateTimeOffset IndexedAt);

    private sealed class WorkspaceSlot : IDisposable
    {
        private readonly string _root;
        private readonly int _maxIndexedFiles;
        private readonly int _maxDeferredIgnoredFiles;
        private readonly Lock _gate = new();

        private volatile WorkspaceSnapshot? _snapshot;
        private long _changeGeneration;
        private long _indexedGeneration = -1;
        private long _lastAccessedTicks = DateTime.UtcNow.Ticks;
        private Task? _inFlightRefresh;
        private HashSet<Action>? _pendingCallbacks;
        private TreeWatcher? _watcher;
        private bool _watcherInitialized;
        private bool _disposed;

        public WorkspaceSlot(string root, int maxIndexedFiles, int maxDeferredIgnoredFiles)
        {
            _root = root;
            _maxIndexedFiles = maxIndexedFiles;
            _maxDeferredIgnoredFiles = maxDeferredIgnoredFiles;
        }

        public WorkspaceSnapshot? Snapshot => _snapshot;

        public long LastAccessedTicks => Volatile.Read(ref _lastAccessedTicks);

        public void Touch() => Volatile.Write(ref _lastAccessedTicks, DateTime.UtcNow.Ticks);

        public void MarkDirty() => Interlocked.Increment(ref _changeGeneration);

        public bool NeedsRefresh(DateTimeOffset now)
        {
            var snapshot = _snapshot;
            if (snapshot is null) return true;
            if (Volatile.Read(ref _changeGeneration) != Volatile.Read(ref _indexedGeneration)) return true;
            return now - snapshot.IndexedAt >= FallbackRefreshInterval;
        }

        public Task EnsureRefreshStarted(Action? onRefreshed)
        {
            lock (_gate)
            {
                if (_disposed) return Task.CompletedTask;

                EnsureWatcherLocked();

                if (_inFlightRefresh is { IsCompleted: false } existing)
                {
                    if (onRefreshed is not null)
                    {
                        (_pendingCallbacks ??= []).Add(onRefreshed);
                    }
                    return existing;
                }

                if (_snapshot is not null && !NeedsRefresh(DateTimeOffset.UtcNow))
                {
                    return Task.CompletedTask;
                }

                if (onRefreshed is not null)
                {
                    (_pendingCallbacks ??= []).Add(onRefreshed);
                }

                var startGen = Volatile.Read(ref _changeGeneration);
                var task = Task.Run(() => RunRefresh(startGen));
                _inFlightRefresh = task;
                return task;
            }
        }

        private void EnsureWatcherLocked()
        {
            if (_watcherInitialized || _disposed) return;
            _watcherInitialized = true;
            if (!Directory.Exists(_root)) return;
            try
            {
                _watcher = new TreeWatcher(_root, _ => MarkDirty());
            }
            catch (Exception)
            {
                _watcher = null;
            }
        }

        private void RunRefresh(long startGeneration)
        {
            IndexedFileEntry[] entries;
            try
            {
                entries = ScanWorkspace(_root, _maxIndexedFiles, _maxDeferredIgnoredFiles);
            }
            catch (Exception)
            {
                entries = [];
            }

            Action[]? callbacks = null;
            lock (_gate)
            {
                if (!_disposed)
                {
                    _snapshot = new WorkspaceSnapshot(_root, entries, DateTimeOffset.UtcNow);
                    Volatile.Write(ref _indexedGeneration, startGeneration);
                    if (_pendingCallbacks is { Count: > 0 })
                    {
                        callbacks = [.. _pendingCallbacks];
                        _pendingCallbacks.Clear();
                    }
                }
                _inFlightRefresh = null;
            }

            if (callbacks is null) return;
            foreach (var callback in callbacks)
            {
                try
                {
                    callback();
                }
                catch (Exception)
                {
                    // A faulty subscriber must not break other tabs waiting on the same workspace index.
                }
            }
        }

        public void Dispose()
        {
            TreeWatcher? watcher;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                watcher = _watcher;
                _watcher = null;
                _pendingCallbacks?.Clear();
            }
            watcher?.Dispose();
        }
    }
}
