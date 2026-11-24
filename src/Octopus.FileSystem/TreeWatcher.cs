namespace Octopus.FileSystem;

/// <summary>
/// One <see cref="FileSystemWatcher"/> over a tree. Creations, deletions and renames are grouped by the directory they
/// happened in and reported once after a quiet period.
/// </summary>
internal sealed class TreeWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);

    private const int MaxPendingDirectories = 64;
    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private readonly string _root;
    private readonly Action<string> _onChanged;
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private bool _disposed;

    public TreeWatcher(string root, Action<string> onChanged)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (_root.Length == 0) _root = Path.DirectorySeparatorChar.ToString();
        _onChanged = onChanged;
        _timer = new Timer(static s => ((TreeWatcher)s!).Flush(), this, Timeout.Infinite, Timeout.Infinite);
        try
        {
            _watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Created += (_, e) => Add(e.FullPath);
            _watcher.Deleted += (_, e) => Add(e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                Add(e.OldFullPath);
                Add(e.FullPath);
            };
            _watcher.Error += (_, _) => Add(null);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            // A folder that cannot be watched simply never reports.
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void Add(string? changedPath)
    {
        string directory;
        if (changedPath is null)
        {
            directory = _root;
        }
        else
        {
            if (IsIgnored(changedPath)) return;
            directory = Path.GetDirectoryName(changedPath) ?? _root;
        }
        lock (_gate)
        {
            if (_disposed) return;
            if (_pending.Count >= MaxPendingDirectories && !_pending.Contains(directory))
            {
                _pending.Clear();
                _pending.Add(_root);
            }
            else
            {
                _pending.Add(directory);
            }
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Ignores everything inside <c>.git</c> and deep churn inside heavy build/dependency folders (while still reporting
    /// creation or removal of those folders themselves at their parent directory).
    /// </summary>
    private bool IsIgnored(string path)
    {
        if (path.Length <= _root.Length) return false;
        var rest = path.AsSpan(_root.Length).TrimStart(Separators);
        while (rest.Length > 0)
        {
            var cut = rest.IndexOfAny(Separators);
            var segment = cut < 0 ? rest : rest[..cut];
            if (segment is ".git") return true;
            // For noisy build/dependency directories, only skip events *inside* them (cut >= 0 means a descendant).
            if (cut >= 0 && IsNoisyDirectory(segment)) return true;
            if (cut < 0) break;
            rest = rest[(cut + 1)..];
        }
        return false;
    }

    private static bool IsNoisyDirectory(ReadOnlySpan<char> segment) =>
        segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
        || segment.Equals("__pycache__", StringComparison.OrdinalIgnoreCase)
        || segment.Equals(".vs", StringComparison.OrdinalIgnoreCase)
        || segment.Equals(".gradle", StringComparison.OrdinalIgnoreCase)
        || segment.Equals(".cache", StringComparison.OrdinalIgnoreCase);

    private void Flush()
    {
        string[] directories;
        lock (_gate)
        {
            if (_disposed) return;
            directories = [.. _pending];
            _pending.Clear();
        }
        foreach (var directory in directories)
        {
            try
            {
                _onChanged(directory);
            }
            catch (Exception)
            {
                // A faulty callback must not stop the others.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
        }
        _watcher?.Dispose();
        _timer.Dispose();
    }
}
