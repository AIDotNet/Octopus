namespace Octopus.FileSystem;

/// <summary>One row of the file tree.</summary>
public sealed record FileEntry(string Name, string FullPath, bool IsDirectory, bool IsHidden);

/// <summary>Lists directories for the file panel. Children are read one directory at a time, when a row is expanded.</summary>
public interface IFileTreeService
{
    /// <summary>
    /// The entries of <paramref name="directory"/>: folders first, then files, each sorted by name the way a file
    /// manager does (case-insensitive, numbers by value). The <c>.git</c> folder is left out. Never throws: an
    /// unreadable or missing directory gives an empty list.
    /// </summary>
    IReadOnlyList<FileEntry> List(string directory);

    /// <summary>
    /// Watches <paramref name="root"/> and everything below it. <paramref name="onChanged"/> gets the directory whose
    /// entries changed (created, deleted or renamed), debounced, on a background thread. Dispose to stop.
    /// </summary>
    IDisposable Watch(string root, Action<string> onChanged);

    /// <summary>
    /// Watches <paramref name="root"/> for any change at all: files created, deleted, renamed or written.
    /// <paramref name="onAnyChange"/> is called once after the changes have stopped for <paramref name="debounce"/>
    /// (at the latest after five seconds of continuous changes), on a background thread. Inside <c>.git</c> only
    /// <c>index</c>, <c>HEAD</c>, <c>packed-refs</c> and <c>refs/</c> count, so <c>git add</c>, <c>commit</c> and
    /// <c>checkout</c> are noticed. Dispose to stop.
    /// </summary>
    IDisposable WatchChanges(string root, Action onAnyChange, TimeSpan debounce);

    /// <summary>
    /// Watches the directory a git repository keeps its state in (found with <c>git rev-parse</c>, so it also works
    /// when the project is a sub-folder of the repository or a linked worktree): <c>HEAD</c>, <c>index</c>,
    /// <c>packed-refs</c> and <c>refs/</c>. <paramref name="commonDirectory"/> is the shared one of a worktree (may be the same).
    /// <paramref name="onChanged"/> is called once after a quiet period, on a background thread.
    /// </summary>
    IDisposable WatchGitDirectory(string gitDirectory, string? commonDirectory, Action onChanged, TimeSpan debounce);

    /// <summary>Watches one file for being written, replaced, renamed or deleted; <paramref name="onChanged"/> is called once after a quiet period, on a background thread.</summary>
    IDisposable WatchFile(string path, Action onChanged, TimeSpan debounce);

    /// <summary>Creates an empty file. Throws <see cref="IOException"/> when the name is taken.</summary>
    Task CreateFileAsync(string path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This file tree cannot create files.");

    /// <summary>Creates a folder. Throws <see cref="IOException"/> when the name is taken.</summary>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This file tree cannot create folders.");

    /// <summary>Renames or moves a file or folder. Throws <see cref="IOException"/> when the new name is taken.</summary>
    Task RenameAsync(string path, string newPath, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This file tree cannot rename.");

    /// <summary>Deletes a file or folder: into the trash where this computer has one, otherwise for good.</summary>
    Task DeleteAsync(string path, bool isDirectory, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This file tree cannot delete.");
}

/// <summary>A line of a file that contains the search text.</summary>
/// <param name="Line">1-based.</param>
/// <param name="Column">0-based index of the match in <paramref name="LineText"/>.</param>
public sealed record ContentMatch(string FilePath, int Line, int Column, string LineText);

/// <summary>Searches a project folder by file name or by file content.</summary>
public interface IFileSearchService
{
    /// <summary>Files and folders whose name contains <paramref name="query"/> (case-insensitive), best matches first.</summary>
    IAsyncEnumerable<FileEntry> SearchNamesAsync(string root, string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lines that contain <paramref name="query"/> (case-insensitive) in text files below <paramref name="root"/>.
    /// Skips binary files, files over 1 MB and the folders nobody wants searched (.git, node_modules, bin, obj, …).
    /// </summary>
    IAsyncEnumerable<ContentMatch> SearchContentAsync(string root, string query, CancellationToken cancellationToken = default);
}
