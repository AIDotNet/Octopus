namespace Octopus.FileSystem;

/// <summary>
/// One <see cref="FileSystemWatcher"/> over a tree that reports that "something changed" once after a quiet period:
/// files created, deleted, renamed or written. Everything inside <c>.git</c> is ignored except the files that say the
/// repository moved: <c>index</c> (git add, commit), <c>HEAD</c> (checkout), <c>packed-refs</c> and <c>refs/</c>.
/// </summary>
internal sealed class ChangeWatcher : IDisposable
{
    /// <summary>A steady stream of writes (a build) still reports after this long.</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(5);

    private readonly string _root;
    private readonly Action _onChange;
    private readonly TimeSpan _debounce;
    private readonly FileSystemWatcher? _watcher;
    private readonly Func<string, bool>? _filter;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private DateTime _firstPending;
    private bool _pending;
    private bool _disposed;

    public ChangeWatcher(string root, Action onChange, TimeSpan debounce, bool recursive = true, Func<string, bool>? filter = null)
    {
        _filter = filter;
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (_root.Length == 0) _root = Path.DirectorySeparatorChar.ToString();
        _onChange = onChange;
        _debounce = debounce;
        _timer = new Timer(static s => ((ChangeWatcher)s!).Flush(), this, Timeout.Infinite, Timeout.Infinite);
        try
        {
            _watcher = new FileSystemWatcher(_root)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Created += (_, e) => Add(e.FullPath);
            _watcher.Deleted += (_, e) => Add(e.FullPath);
            _watcher.Changed += (_, e) => Add(e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                Add(e.OldFullPath);
                Add(e.FullPath);
            };
            _watcher.Error += (_, _) => Schedule();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            // A folder that cannot be watched simply never reports.
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    private void Add(string path)
    {
        if (IsRelevant(path)) Schedule();
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var now = DateTime.UtcNow;
            if (!_pending)
            {
                _pending = true;
                _firstPending = now;
            }
            var wait = _debounce;
            var left = _firstPending + MaxWait - now;
            if (left < wait) wait = left > TimeSpan.Zero ? left : TimeSpan.Zero;
            _timer.Change(wait, Timeout.InfiniteTimeSpan);
        }
    }

    private bool IsRelevant(string path)
    {
        if (_filter is not null) return _filter(path);
        if (path.Length <= _root.Length) return true;
        var relative = path.Substring(_root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var git = Array.IndexOf(parts, ".git");
        if (git < 0) return true;
        // Inside .git only the few files that tell the repository changed are worth a refresh.
        if (git + 1 >= parts.Length) return true;
        return parts[git + 1] switch
        {
            "index" or "HEAD" or "packed-refs" => git + 2 == parts.Length,
            "refs" => true,
            _ => false,
        };
    }

    private void Flush()
    {
        lock (_gate)
        {
            if (_disposed || !_pending) return;
            _pending = false;
        }
        try
        {
            _onChange();
        }
        catch (Exception)
        {
            // A faulty callback must not take the timer down.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _watcher?.Dispose();
        _timer.Dispose();
    }
}
