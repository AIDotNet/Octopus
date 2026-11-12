using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
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

    private readonly IFileTreeService _service;
    private readonly IFileOpener _opener;
    private readonly PathComparer _paths;
    private readonly Dictionary<string, FileNodeViewModel> _nodes = new(StringComparer.Ordinal);
    private Dictionary<string, GitChangeKind> _changedFiles = new(StringComparer.Ordinal);
    private HashSet<string> _changedFolders = new(StringComparer.Ordinal);

    public FileTreeViewModel(IFileTreeService service, IFileOpener opener, PathComparer paths)
    {
        _service = service;
        _opener = opener;
        _paths = paths;
    }

    /// <summary>The project folder; null when there is none.</summary>
    public string? Root { get; private set; }

    public ObservableCollection<FileNodeViewModel> Roots { get; } = [];

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

    partial void OnContextNodeChanged(FileNodeViewModel? value)
    {
        OpenContextCommand.NotifyCanExecuteChanged();
        RevealContextCommand.NotifyCanExecuteChanged();
        CopyContextPathCommand.NotifyCanExecuteChanged();
        CopyContextRelativePathCommand.NotifyCanExecuteChanged();
    }

    public void Load(string? root)
    {
        Roots.Clear();
        _nodes.Clear();
        SelectedNode = null;
        ContextNode = null;
        _changedFiles = new(StringComparer.Ordinal);
        _changedFolders = new(StringComparer.Ordinal);
        Root = root;
        if (root is not null) Refresh(null, populateFolders: true);
    }

    /// <summary>A folder opens: reads it when that has not happened yet, and the folders inside it.</summary>
    public void EnsureLoaded(FileNodeViewModel node)
    {
        if (!node.IsDirectory) return;
        if (!node.IsLoaded) Refresh(node, populateFolders: true);
        else PopulateFolders(node.Children);
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

    /// <summary>Rereads every folder that has been opened.</summary>
    [RelayCommand]
    public void Reload()
    {
        if (Root is null) return;
        Refresh(null, populateFolders: true);
        foreach (var node in Roots.ToList()) ReloadNode(node);
    }

    [RelayCommand]
    public void CollapseAll()
    {
        foreach (var node in _nodes.Values) node.IsExpanded = false;
        CollapseAllRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public void ToggleHidden() => ShowHidden = !ShowHidden;

    [RelayCommand]
    public void RevealProject()
    {
        if (Root is not null) _opener.Reveal(Root);
    }

    /// <summary>Double-click or Enter on a row: a file is opened, a folder is left to the tree.</summary>
    public void RequestOpen(FileNodeViewModel? node)
    {
        if (node is { IsDirectory: false }) OpenRequested?.Invoke(this, node.FullPath);
    }

    private bool HasContextFile => ContextNode is not null;

    [RelayCommand(CanExecute = nameof(CanOpenContext))]
    private void OpenContext() => RequestOpen(ContextNode);

    private bool CanOpenContext() => ContextNode is { IsDirectory: false };

    [RelayCommand(CanExecute = nameof(HasContextFile))]
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

    /// <summary>Marks changed files with their status letter and the folders that contain them with a dot.</summary>
    public void ApplyGitStatus(GitStatus? status, string projectRealPath)
    {
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
        foreach (var node in _nodes.Values) Decorate(node);
    }

    private void Decorate(FileNodeViewModel node)
    {
        if (Root is null) return;
        var key = RealPath.Relative(Root, node.FullPath);
        if (key is null)
        {
            node.ApplyGit(null, false);
            return;
        }
        GitChangeKind? kind = !node.IsDirectory && _changedFiles.TryGetValue(key, out var k) ? k : null;
        node.ApplyGit(kind, node.IsDirectory && _changedFolders.Contains(key));
    }

    private void ReloadNode(FileNodeViewModel node)
    {
        if (!node.IsDirectory || !node.IsLoaded) return;
        Refresh(node, node.IsExpanded);
        foreach (var child in node.Children.ToList()) ReloadNode(child);
    }

    /// <summary>
    /// Rereads one folder (null: the project folder) and brings its rows in line, keeping the rows that remain. With
    /// <paramref name="populateFolders"/> the folders in it are read as well, one level.
    /// </summary>
    private void Refresh(FileNodeViewModel? parent, bool populateFolders)
    {
        var path = parent?.FullPath ?? Root;
        if (path is null) return;
        var rows = parent?.Children ?? Roots;
        var entries = _service.List(path).Where(e => ShowHidden || !e.IsHidden).ToList();

        var existing = new Dictionary<(string, bool), FileNodeViewModel>();
        foreach (var node in rows)
        {
            existing[(node.FullPath, node.IsDirectory)] = node;
        }
        var desired = new List<FileNodeViewModel>(entries.Count);
        foreach (var entry in entries)
        {
            if (!existing.Remove((entry.FullPath, entry.IsDirectory), out var node))
            {
                node = new FileNodeViewModel(entry);
                _nodes[node.FullPath] = node;
                Decorate(node);
            }
            desired.Add(node);
        }
        // What is left in `existing` is gone from disk (or hidden now).
        foreach (var gone in existing.Values) Forget(gone);
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(rows[i])) rows.RemoveAt(i);
        }
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < rows.Count && ReferenceEquals(rows[i], desired[i])) continue;
            var at = rows.IndexOf(desired[i]);
            if (at >= 0) rows.Move(at, i);
            else rows.Insert(i, desired[i]);
        }
        if (parent is not null) parent.IsLoaded = true;
        if (populateFolders) PopulateFolders(rows);
    }

    /// <summary>Reads the folders among <paramref name="rows"/> that have not been read yet (without their own folders).</summary>
    private void PopulateFolders(ObservableCollection<FileNodeViewModel> rows)
    {
        foreach (var folder in rows.Where(r => r is { IsDirectory: true, IsLoaded: false }).ToList())
        {
            Refresh(folder, populateFolders: false);
            // The tree reads whether a row has children when it builds the row, and watches only the children of open
            // folders. A folder that just got children is "replaced" by itself, so the tree builds its row again.
            if (folder.Children.Count > 0 && rows.IndexOf(folder) is var at and >= 0) rows[at] = folder;
        }
    }

    private void Forget(FileNodeViewModel node)
    {
        _nodes.Remove(node.FullPath);
        foreach (var child in node.Children) Forget(child);
        if (ReferenceEquals(SelectedNode, node)) SelectedNode = null;
    }
}
