using Octopus.Platform;

namespace Octopus.FileSystem;

/// <summary>Lists directories with <see cref="Directory"/> and watches a tree with <see cref="FileSystemWatcher"/>.</summary>
public sealed class FileTreeService(ITrashService? trash = null) : IFileTreeService
{
    public async Task CreateFileAsync(string path, CancellationToken cancellationToken = default) =>
        await Task.Run(() =>
        {
            EnsureFree(path);
            using (File.Create(path))
            {
            }
        }, cancellationToken);

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        await Task.Run(() =>
        {
            EnsureFree(path);
            Directory.CreateDirectory(path);
        }, cancellationToken);

    public async Task RenameAsync(string path, string newPath, CancellationToken cancellationToken = default) =>
        await Task.Run(() =>
        {
            var isDirectory = Directory.Exists(path);
            // A name that differs only in case is the same entry on a case-insensitive disk: it is moved through a temporary name.
            if (string.Equals(path, newPath, StringComparison.OrdinalIgnoreCase) && !string.Equals(path, newPath, StringComparison.Ordinal))
            {
                var temporary = path + ".octopus-rename";
                EnsureFree(temporary);
                Move(path, temporary, isDirectory);
                path = temporary;
            }
            else
            {
                EnsureFree(newPath);
            }
            Move(path, newPath, isDirectory);
        }, cancellationToken);

    public async Task DeleteAsync(string path, bool isDirectory, CancellationToken cancellationToken = default) =>
        await Task.Run(() =>
        {
            if (trash is not null && trash.MoveToTrash(path)) return;
            if (isDirectory) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }, cancellationToken);

    private static void EnsureFree(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException($"The name is taken: {Path.GetFileName(path)}");
        }
    }

    private static void Move(string from, string to, bool isDirectory)
    {
        if (isDirectory) Directory.Move(from, to);
        else File.Move(from, to);
    }

    public IReadOnlyList<FileEntry> List(string directory)
    {
        try
        {
            var entries = new List<FileEntry>();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(path);
                bool isDirectory;
                var hidden = name.StartsWith('.');
                try
                {
                    var info = new FileInfo(path);
                    var attributes = info.Attributes;
                    hidden |= (attributes & FileAttributes.Hidden) != 0;
                    // A link to a folder is listed as a folder: expanding it reads the target once; the tree is lazy,
                    // so a cycle only costs the depth the person opens.
                    isDirectory = (attributes & FileAttributes.Directory) != 0;
                }
                catch (Exception)
                {
                    isDirectory = Directory.Exists(path);
                }
                if (isDirectory && name == ".git") continue;
                entries.Add(new FileEntry(name, path, isDirectory, hidden));
            }
            entries.Sort(static (a, b) =>
                a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : NaturalStringComparer.Instance.Compare(a.Name, b.Name));
            return entries;
        }
        catch (Exception)
        {
            return [];
        }
    }

    public IDisposable Watch(string root, Action<string> onChanged) => new TreeWatcher(root, onChanged);

    public IDisposable WatchChanges(string root, Action onAnyChange, TimeSpan debounce) => new ChangeWatcher(root, onAnyChange, debounce);

    public IDisposable WatchFile(string path, Action onChanged, TimeSpan debounce)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (directory is null) return new CompositeWatcher([]);
        return new ChangeWatcher(directory, onChanged, debounce, recursive: false,
            filter: changed => string.Equals(Path.GetFullPath(changed), full, comparison));
    }

    public IDisposable WatchGitDirectory(string gitDirectory, string? commonDirectory, Action onChanged, TimeSpan debounce)
    {
        var watchers = new List<IDisposable>();
        var directories = new List<string> { gitDirectory };
        if (commonDirectory is not null && !string.Equals(Path.GetFullPath(commonDirectory), Path.GetFullPath(gitDirectory), StringComparison.Ordinal))
        {
            directories.Add(commonDirectory);
        }
        foreach (var directory in directories)
        {
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // HEAD, index (git writes index.lock and renames it) and packed-refs sit right in the directory; the refs below it.
            watchers.Add(new ChangeWatcher(root, onChanged, debounce, recursive: false, filter: path =>
                Path.GetFileName(path) is "HEAD" or "index" or "packed-refs"));
            var refs = Path.Combine(root, "refs");
            if (Directory.Exists(refs))
            {
                watchers.Add(new ChangeWatcher(refs, onChanged, debounce, recursive: true, filter: path => !path.EndsWith(".lock", StringComparison.Ordinal)));
            }
        }
        return new CompositeWatcher(watchers);
    }

    private sealed class CompositeWatcher(IReadOnlyList<IDisposable> parts) : IDisposable
    {
        public void Dispose()
        {
            foreach (var part in parts) part.Dispose();
        }
    }
}
