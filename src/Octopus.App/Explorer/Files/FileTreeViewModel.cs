using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.FileSystem;
using Octopus.Git;
using Octopus.Platform;

namespace Octopus.App.Explorer.Files;

/// <summary>
/// The file tree of the project folder: refresh by directory and git decoration. Folders are read one step ahead: when a
/// folder is shown, the folders in it are listed too, so that the tree draws their arrows (only folders with content
/// have one) and opening one never shows an empty row first.
/// </summary>
public sealed partial class FileTreeViewModel : ObservableObject
{
    /// <summary>More untracked files than this are not marked in the tree (they are listed in the Git tab): a repository with no commits yet would mark every row.</summary>
    public const int UntrackedMarkLimit = 200;

    private readonly IFileTreeService _local;
    /// <summary>Where the folders are read from: this computer, or an SSH host for the project being shown.</summary>
    private IFileTreeService _service;
    private readonly IFileOpener _opener;
    private readonly IDialogService _dialogs;
    private readonly PathComparer _paths;
    private readonly Dictionary<string, FileNodeViewModel> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileNodeViewModel> _nodesByRelativePath = new(StringComparer.Ordinal);
    private readonly HashSet<FileNodeViewModel> _decoratedNodes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<string> _inFlightDirs = new(StringComparer.Ordinal);
    private Dictionary<string, GitChangeKind> _changedFiles = new(StringComparer.Ordinal);
    private HashSet<string> _changedFolders = new(StringComparer.Ordinal);
    private IReadOnlyList<GitChange>? _appliedChanges;
    /// <summary>Bumped by <see cref="Load"/>: read-ahead and async refreshes from an earlier folder are dropped.</summary>
    private int _generation;
    private Task _readAhead = Task.CompletedTask;

    /// <summary>Folders read at once in the background (a local disk, or the runs of an SSH tree).</summary>
    private const int ReadAheadParallelism = 4;

    /// <summary>Maximum child folders read ahead per parent directory so huge flat folders don't storm the disk.</summary>
    private const int MaxReadAheadFolders = 128;

    /// <summary>Diffs larger than this use a single batch Reset instead of per-item collection events.</summary>
    private const int BatchReplaceThreshold = 32;

    private readonly BatchObservableCollection<FileNodeViewModel> _roots = [];

    public FileTreeViewModel(IFileTreeService service, IFileOpener opener, PathComparer paths, IDialogService dialogs)
    {
        _dialogs = dialogs;
        _local = service;
        _service = service;
        _opener = opener;
        _paths = paths;
    }

    /// <summary>The project folder; null when there is none.</summary>
    public string? Root { get; private set; }

    /// <summary>The folders are on an SSH host: "Reveal" has nothing to show on this computer.</summary>
    [ObservableProperty] public partial bool IsRemote { get; private set; }

    public ObservableCollection<FileNodeViewModel> Roots => _roots;

    [ObservableProperty] public partial FileNodeViewModel? SelectedNode { get; set; }

    /// <summary>The row the context menu was opened on.</summary>
    [ObservableProperty] public partial FileNodeViewModel? ContextNode { get; set; }

    /// <summary>Hidden files (names that start with a dot) are shown, in the secondary text color.</summary>
    [ObservableProperty] public partial bool ShowHidden { get; set; } = true;

    /// <summary>Raised with the absolute path of a file to open.</summary>
    public event EventHandler<string>? OpenRequested;

    /// <summary>Raised with text the view puts on the clipboard.</summary>
    public event EventHandler<string>? CopyRequested;

    /// <summary>Raised when every folder should close.</summary>
    public event EventHandler? CollapseAllRequested;

    partial void OnShowHiddenChanged(bool value) => Reload();

    partial void OnContextNodeChanged(FileNodeViewModel? value) => NotifyContextCommands();

    private void NotifyContextCommands()
    {
        OpenContextCommand.NotifyCanExecuteChanged();
        RevealContextCommand.NotifyCanExecuteChanged();
        CopyContextPathCommand.NotifyCanExecuteChanged();
        CopyContextRelativePathCommand.NotifyCanExecuteChanged();
        NewFileHereCommand.NotifyCanExecuteChanged();
        NewFolderHereCommand.NotifyCanExecuteChanged();
        RenameContextCommand.NotifyCanExecuteChanged();
        DeleteContextCommand.NotifyCanExecuteChanged();
    }

    /// <param name="root">The folder to show (null: none).</param>
    /// <param name="service">Where its folders are read from; null reads this computer.</param>
    public void Load(string? root, IFileTreeService? service = null)
    {
        _generation++;
        _roots.Clear();
        _nodes.Clear();
        _nodesByRelativePath.Clear();
        _decoratedNodes.Clear();
        _inFlightDirs.Clear();
        _appliedChanges = null;
        SelectedNode = null;
        ContextNode = null;
        _changedFiles = new(StringComparer.Ordinal);
        _changedFolders = new(StringComparer.Ordinal);
        Root = root;
        _service = service ?? _local;
        IsRemote = service is not null;
        RevealProjectCommand.NotifyCanExecuteChanged();
        NotifyContextCommands();
        if (root is not null) Refresh(null, populateFolders: true);
    }

    /// <summary>Completes when the folders being read ahead in the background have been read and shown.</summary>
    internal Task ReadAheadIdle => _readAhead;

    /// <summary>A folder opens: reads it when that has not happened yet, and the folders inside it.</summary>
    public void EnsureLoaded(FileNodeViewModel node)
    {
        if (!node.IsDirectory) return;
        if (!node.IsLoaded) Refresh(node, populateFolders: true);
        else PopulateFolders(node.BatchChildren);
    }

    /// <summary>Async variant of <see cref="EnsureLoaded"/> used by UI expansion so large folders never block the UI thread.</summary>
    public async Task EnsureLoadedAsync(FileNodeViewModel node)
    {
        if (!node.IsDirectory) return;
        if (node.IsLoaded)
        {
            PopulateFolders(node.BatchChildren);
            return;
        }
        var generation = _generation;
        var service = _service;
        var listing = await Task.Run(() => service.List(node.FullPath));
        if (generation != _generation) return;
        if (!_nodes.TryGetValue(node.FullPath, out var known) || !ReferenceEquals(known, node)) return;
        Apply(node, node.BatchChildren, listing);
        PopulateFolders(node.BatchChildren);
    }

    /// <summary>A directory changed on disk: reread it when it is on screen, keeping what is open and selected.</summary>
    public void RefreshDirectory(string directory)
    {
        if (Root is null) return;
        if (_paths.Equals(directory, Root))
        {
            Refresh(null, populateFolders: true);
        }
        else if (_nodes.TryGetValue(directory, out var node) && node.IsLoaded)
        {
            Refresh(node, node.IsExpanded);
        }
    }

    /// <summary>
    /// Background-thread directory refresh for file-system watcher bursts and file operations: lists the directory off
    /// the UI thread, coalesces duplicate in-flight reads for the same path, and applies the diff back on the UI thread.
    /// </summary>
    public async Task RefreshDirectoryAsync(string directory)
    {
        if (Root is null) return;
        var isRoot = _paths.Equals(directory, Root);
        FileNodeViewModel? target = null;
        if (!isRoot && (!_nodes.TryGetValue(directory, out target) || !target.IsLoaded))
        {
            return;
        }
        var key = isRoot ? Root : target!.FullPath;
        if (!_inFlightDirs.Add(key)) return;

        var generation = _generation;
        var service = _service;
        try
        {
            var listing = await Task.Run(() => service.List(key));
            if (generation != _generation) return;
            if (isRoot)
            {
                Apply(null, _roots, listing);
                PopulateFolders(_roots);
            }
            else if (_nodes.TryGetValue(key, out var current) && current.IsLoaded)
            {
                Apply(current, current.BatchChildren, listing);
                if (current.IsExpanded) PopulateFolders(current.BatchChildren);
            }
        }
        finally
        {
            _inFlightDirs.Remove(key);
        }
    }

    /// <summary>Rereads every folder that has been opened.</summary>
    [RelayCommand]
    public void Reload()
    {
        if (Root is null) return;
        Refresh(null, populateFolders: true);
        foreach (var node in _roots.ToList()) ReloadNode(node);
    }

    /// <summary>Rereads every loaded folder in parallel off the UI thread, then applies updates top-down.</summary>
    public async Task ReloadAsync()
    {
        if (Root is null) return;
        var generation = _generation;
        var service = _service;
        var rootPath = Root;

        var targets = new List<(FileNodeViewModel? Node, string Path)> { (null, rootPath) };
        CollectLoadedFolders(_roots, targets);

        var listings = new IReadOnlyList<FileEntry>[targets.Count];
        await Task.Run(() => Parallel.For(0, targets.Count, new ParallelOptions { MaxDegreeOfParallelism = ReadAheadParallelism },
            i => listings[i] = service.List(targets[i].Path)));

        if (generation != _generation) return;

        Apply(null, _roots, listings[0]);
        for (var i = 1; i < targets.Count; i++)
        {
            var node = targets[i].Node!;
            if (_nodes.TryGetValue(node.FullPath, out var known) && ReferenceEquals(known, node) && node.IsLoaded)
            {
                Apply(node, node.BatchChildren, listings[i]);
            }
        }
        PopulateFolders(_roots);
        for (var i = 1; i < targets.Count; i++)
        {
            var node = targets[i].Node!;
            if (node.IsExpanded && _nodes.TryGetValue(node.FullPath, out var known) && ReferenceEquals(known, node))
            {
                PopulateFolders(node.BatchChildren);
            }
        }
    }

    private static void CollectLoadedFolders(IReadOnlyList<FileNodeViewModel> rows, List<(FileNodeViewModel? Node, string Path)> targets)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            var node = rows[i];
            if (!node.IsDirectory || !node.IsLoaded) continue;
            targets.Add((node, node.FullPath));
            CollectLoadedFolders(node.BatchChildren, targets);
        }
    }

    [RelayCommand]
    public void CollapseAll()
    {
        foreach (var node in _nodes.Values) node.IsExpanded = false;
        CollapseAllRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void ToggleHidden() => ShowHidden = !ShowHidden;

    partial void OnIsRemoteChanged(bool value)
    {
        RevealProjectCommand.NotifyCanExecuteChanged();
        RevealContextCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRevealProject))]
    public void RevealProject()
    {
        if (Root is not null) _opener.Reveal(Root);
    }

    /// <summary>Click or Enter on a row: a file is opened, a folder is left to the tree.</summary>
    public void RequestOpen(FileNodeViewModel? node)
    {
        if (node is { IsDirectory: false }) OpenRequested?.Invoke(this, node.FullPath);
    }

    private bool HasContextFile => ContextNode is not null;

    private bool CanRevealProject() => Root is not null && !IsRemote;

    private bool CanRevealContext() => HasContextFile && !IsRemote;

    [RelayCommand(CanExecute = nameof(CanOpenContext))]
    private void OpenContext() => RequestOpen(ContextNode);

    private bool CanOpenContext() => ContextNode is { IsDirectory: false };

    [RelayCommand(CanExecute = nameof(CanRevealContext))]
    private void RevealContext()
    {
        if (ContextNode is { } node) _opener.Reveal(node.FullPath);
    }

    [RelayCommand(CanExecute = nameof(HasContextFile))]
    private void CopyContextPath()
    {
        if (ContextNode is { } node) CopyRequested?.Invoke(this, node.FullPath);
    }

    [RelayCommand(CanExecute = nameof(HasContextFile))]
    private void CopyContextRelativePath()
    {
        if (ContextNode is { } node && Root is not null)
            CopyRequested?.Invoke(this, RealPath.Relative(Root, node.FullPath) ?? node.FullPath);
    }

    // ---- file operations of the context menu: this computer's files, or the files of the SSH host ----

    /// <summary>The folder a new file or folder goes into: the folder of the row, or the project folder.</summary>
    private string? CreationFolder => ContextNode is null ? Root : ContextNode.IsDirectory ? ContextNode.FullPath : ParentOf(ContextNode.FullPath);

    private bool CanCreate => Root is not null;

    private bool HasContextRow => ContextNode is not null;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task NewFileHereAsync() => CreateAsync(isDirectory: false);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task NewFolderHereAsync() => CreateAsync(isDirectory: true);

    [RelayCommand(CanExecute = nameof(HasContextRow))]
    private Task RenameContextAsync() => RenameAsync(ContextNode!);

    [RelayCommand(CanExecute = nameof(HasContextRow))]
    private Task DeleteContextAsync() => DeleteAsync(ContextNode!);

    private async Task CreateAsync(bool isDirectory)
    {
        var folder = CreationFolder;
        if (folder is null) return;
        var name = await _dialogs.AskNameAsync(
            AppLangResources.Get(isDirectory ? AppLangResourceKind.ExplorerNewFolderTitle : AppLangResourceKind.ExplorerNewFileTitle),
            AppLangResources.Get(AppLangResourceKind.ExplorerNameLabel),
            null,
            AppLangResources.Get(AppLangResourceKind.ExplorerCreateConfirm));
        if (name is null) return;
        if (!ValidName(name))
        {
            await ReportAsync(AppLangResources.Get(AppLangResourceKind.ExplorerInvalidName));
            return;
        }
        var target = Join(folder, name);
        await DoAsync(folder, () => isDirectory ? _service.CreateDirectoryAsync(target) : _service.CreateFileAsync(target));
    }

    private async Task RenameAsync(FileNodeViewModel node)
    {
        var parent = ParentOf(node.FullPath);
        var name = await _dialogs.AskNameAsync(
            AppLangResources.Get(AppLangResourceKind.ExplorerRenameTitle),
            AppLangResources.Get(AppLangResourceKind.ExplorerNameLabel),
            node.Name,
            AppLangResources.Get(AppLangResourceKind.ExplorerRenameConfirm));
        if (name is null || name == node.Name) return;
        if (!ValidName(name))
        {
            await ReportAsync(AppLangResources.Get(AppLangResourceKind.ExplorerInvalidName));
            return;
        }
        var target = Join(parent, name);
        await DoAsync(parent, () => _service.RenameAsync(node.FullPath, target));
    }

    private async Task DeleteAsync(FileNodeViewModel node)
    {
        var body = AppLangResources.Get(IsRemote ? AppLangResourceKind.ExplorerDeleteRemoteBody : AppLangResourceKind.ExplorerDeleteLocalBody);
        if (node.IsDirectory) body += " " + AppLangResources.Get(AppLangResourceKind.ExplorerDeleteFolderSuffix);
        var confirmed = await _dialogs.ConfirmAsync(
            AppLangResources.Format(AppLangResourceKind.ExplorerDeleteTitle, node.Name),
            body,
            AppLangResources.Get(AppLangResourceKind.ExplorerDeleteConfirm),
            isDanger: true);
        if (!confirmed) return;
        var parent = ParentOf(node.FullPath);
        await DoAsync(parent, () => _service.DeleteAsync(node.FullPath, node.IsDirectory));
    }

    /// <summary>Runs one change and then reads its folder again; a change that fails is said, and nothing else changes.</summary>
    private async Task DoAsync(string folder, Func<Task> change)
    {
        try
        {
            await change();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await ReportAsync(e.Message);
        }
        await RefreshDirectoryAsync(folder);
    }

    private Task ReportAsync(string message) =>
        _dialogs.ConfirmAsync(
            AppLangResources.Get(AppLangResourceKind.ExplorerOperationFailed),
            message,
            AppLangResources.Get(AppLangResourceKind.RemoteOpenFailedClose));

    /// <summary>Joined the way the tree's paths are: "/" on the host, the platform's on this computer.</summary>
    private string Join(string folder, string name) => IsRemote ? folder.TrimEnd('/') + "/" + name : Path.Combine(folder, name);

    private string ParentOf(string path)
    {
        if (IsRemote)
        {
            var index = path.LastIndexOf('/');
            return index <= 0 ? "/" : path[..index];
        }
        return Path.GetDirectoryName(path) ?? path;
    }

    private static bool ValidName(string name) => name.Length > 0 && name is not ("." or "..") && name.IndexOfAny(['/', '\\', '\0']) < 0;

    /// <summary>Marks changed files with their status letter and the folders that contain them with a dot.</summary>
    public void ApplyGitStatus(GitStatus? status, string projectRealPath)
    {
        // A refresh that found the same changes (most of them, during a build) leaves every row as it is.
        var changes = status?.Changes;
        if (changes is not null && _appliedChanges is not null && changes.SequenceEqual(_appliedChanges)) return;
        _appliedChanges = changes;
        var files = new Dictionary<string, GitChangeKind>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        if (status is not null)
        {
            var markUntracked = status.Changes.Count(c => c.Kind == GitChangeKind.Untracked) <= UntrackedMarkLimit;
            // The unstaged side wins when a file has both: it is what the person still has to deal with.
            foreach (var change in status.Changes.OrderBy(c => c.IsStaged ? 0 : 1))
            {
                if (change.Kind == GitChangeKind.Untracked && !markUntracked) continue;
                if (RealPath.Relative(projectRealPath, change.FullPath) is not { } relative) continue;
                files[relative] = change.Kind;
                // An untracked folder listed whole is itself marked with the dot.
                if (change.IsDirectory) folders.Add(relative);
                for (var i = relative.LastIndexOf('/'); i > 0; i = relative.LastIndexOf('/', i - 1))
                {
                    folders.Add(relative[..i]);
                }
            }
        }
        _changedFiles = files;
        _changedFolders = folders;

        // Incremental decoration: only touch previously decorated nodes and newly affected paths — O(changed), not O(all nodes).
        var affected = new HashSet<FileNodeViewModel>(_decoratedNodes, ReferenceEqualityComparer.Instance);
        foreach (var path in files.Keys)
        {
            if (_nodesByRelativePath.TryGetValue(path, out var node)) affected.Add(node);
        }
        foreach (var path in folders)
        {
            if (_nodesByRelativePath.TryGetValue(path, out var node)) affected.Add(node);
        }
        _decoratedNodes.Clear();
        foreach (var node in affected)
        {
            Decorate(node);
        }
    }

    private void Decorate(FileNodeViewModel node)
    {
        var key = node.RelativePath;
        if (key is null)
        {
            node.ApplyGit(null, false);
            return;
        }
        GitChangeKind? kind = !node.IsDirectory && _changedFiles.TryGetValue(key, out var k) ? k : null;
        var hasChangedDescendant = node.IsDirectory && _changedFolders.Contains(key);
        node.ApplyGit(kind, hasChangedDescendant);
        if (kind is not null || hasChangedDescendant) _decoratedNodes.Add(node);
    }

    private void ReloadNode(FileNodeViewModel node)
    {
        if (!node.IsDirectory || !node.IsLoaded) return;
        Refresh(node, node.IsExpanded);
        foreach (var child in node.BatchChildren.ToList()) ReloadNode(child);
    }

    /// <summary>
    /// Rereads one folder (null: the project folder) and brings its rows in line, keeping the rows that remain. With
    /// <paramref name="populateFolders"/> the folders in it are read as well, one level, in the background.
    /// </summary>
    private void Refresh(FileNodeViewModel? parent, bool populateFolders)
    {
        var path = parent?.FullPath ?? Root;
        if (path is null) return;
        var rows = parent?.BatchChildren ?? _roots;
        Apply(parent, rows, _service.List(path));
        if (populateFolders) PopulateFolders(rows);
    }

    /// <summary>Brings <paramref name="rows"/> in line with <paramref name="listing"/>, keeping the rows that remain.</summary>
    private void Apply(FileNodeViewModel? parent, BatchObservableCollection<FileNodeViewModel> rows, IReadOnlyList<FileEntry> listing)
    {
        var existing = new Dictionary<(string, bool), FileNodeViewModel>(rows.Count);
        foreach (var node in rows)
        {
            existing[(node.FullPath, node.IsDirectory)] = node;
        }
        var parentRelative = parent?.RelativePath;
        var desired = new List<FileNodeViewModel>(listing.Count);
        var addedCount = 0;
        foreach (var entry in listing)
        {
            if (!ShowHidden && entry.IsHidden) continue;
            if (!existing.Remove((entry.FullPath, entry.IsDirectory), out var node))
            {
                var rel = string.IsNullOrEmpty(parentRelative) ? entry.Name : parentRelative + "/" + entry.Name;
                node = new FileNodeViewModel(entry, rel);
                _nodes[node.FullPath] = node;
                _nodesByRelativePath[rel] = node;
                Decorate(node);
                addedCount++;
            }
            desired.Add(node);
        }
        // What is left in `existing` is gone from disk (or hidden now).
        var removedCount = existing.Count;
        foreach (var gone in existing.Values) Forget(gone);

        // Large diffs or initial populations replace in one batch Reset to avoid thousands of per-item CollectionChanged events.
        if (rows.Count == 0 || desired.Count == 0 || addedCount + removedCount > BatchReplaceThreshold)
        {
            if (rows.Count != 0 || desired.Count != 0)
            {
                rows.ReplaceAll(desired);
            }
        }
        else
        {
            // Small live diffs mutate in place so Arc TreeView can smoothly animate added/removed rows.
            var wanted = new HashSet<FileNodeViewModel>(desired, ReferenceEqualityComparer.Instance);
            var kept = new HashSet<FileNodeViewModel>(ReferenceEqualityComparer.Instance);
            for (var i = rows.Count - 1; i >= 0; i--)
            {
                if (wanted.Contains(rows[i])) kept.Add(rows[i]);
                else rows.RemoveAt(i);
            }
            for (var i = 0; i < desired.Count; i++)
            {
                var node = desired[i];
                if (i < rows.Count && ReferenceEquals(rows[i], node)) continue;
                if (!kept.Contains(node))
                {
                    rows.Insert(i, node);
                    continue;
                }
                // Rows before i are in place, so a kept row that is out of place is further down.
                var at = i + 1;
                while (!ReferenceEquals(rows[at], node)) at++;
                rows.Move(at, i);
            }
        }
        if (parent is not null) parent.IsLoaded = true;
    }

    private static readonly HashSet<string> SkipReadAhead = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".venv", "venv", "target", ".gradle", "__pycache__",
        "bin", "obj", "dist", "build", ".next", ".nuxt", ".cache", "vendor", "Pods",
        ".dart_tool", ".mypy_cache", ".pytest_cache", ".ruff_cache",
    };

    /// <summary>
    /// Reads the folders among <paramref name="rows"/> that have not been read yet (without their own folders), off the
    /// UI thread: opening a folder with hundreds of folders in it would otherwise wait for hundreds of listings.
    /// </summary>
    private void PopulateFolders(BatchObservableCollection<FileNodeViewModel> rows)
    {
        var folders = rows
            .Where(r => r is { IsDirectory: true, IsLoaded: false } && !SkipReadAhead.Contains(r.Name))
            .Take(MaxReadAheadFolders)
            .ToArray();
        if (folders.Length == 0) return;
        var task = ReadAheadAsync(rows, folders, _service, _generation);
        _readAhead = _readAhead.IsCompleted ? task : Task.WhenAll(_readAhead, task);
    }

    private async Task ReadAheadAsync(BatchObservableCollection<FileNodeViewModel> rows, FileNodeViewModel[] folders, IFileTreeService service, int generation)
    {
        var listings = new IReadOnlyList<FileEntry>[folders.Length];
        await Task.Run(() => Parallel.For(0, folders.Length, new ParallelOptions { MaxDegreeOfParallelism = ReadAheadParallelism },
            i => listings[i] = service.List(folders[i].FullPath)));
        // Back on the UI thread. Another project, or a folder that was read (opened) or dropped meanwhile, is left alone.
        if (generation != _generation) return;
        var anyPopulated = false;
        for (var i = 0; i < folders.Length; i++)
        {
            var folder = folders[i];
            if (folder.IsLoaded || !_nodes.TryGetValue(folder.FullPath, out var known) || !ReferenceEquals(known, folder)) continue;
            Apply(folder, folder.BatchChildren, listings[i]);
            if (folder.BatchChildren.Count > 0) anyPopulated = true;
        }
        // The tree reads whether a row has children when it rebuilds rows; one batch Reset notifies all populated folders in O(1).
        if (anyPopulated) rows.NotifyReset();
    }

    private void Forget(FileNodeViewModel node)
    {
        _nodes.Remove(node.FullPath);
        if (node.RelativePath is { } rel) _nodesByRelativePath.Remove(rel);
        _decoratedNodes.Remove(node);
        if (node.IsDirectory && node.BatchChildren.Count > 0)
        {
            foreach (var child in node.BatchChildren) Forget(child);
        }
        if (ReferenceEquals(SelectedNode, node)) SelectedNode = null;
        if (ReferenceEquals(ContextNode, node)) ContextNode = null;
    }
}
