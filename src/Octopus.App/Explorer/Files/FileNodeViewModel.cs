using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Common;
using Octopus.FileSystem;
using Octopus.Git;
using TKCode.Arc.Icons.Lucide;

namespace Octopus.App.Explorer.Files;

/// <summary>A row of the file tree. The children of a folder are read one step ahead of the person (see <see cref="FileTreeViewModel"/>).</summary>
public sealed partial class FileNodeViewModel : ObservableObject
{
    private readonly string _closedIconKey;
    private readonly string _openIconKey;

    public FileNodeViewModel(FileEntry entry, string? relativePath = null)
    {
        Name = entry.Name;
        FullPath = entry.FullPath;
        RelativePath = relativePath;
        IsDirectory = entry.IsDirectory;
        IsHidden = entry.IsHidden;
        _closedIconKey = FileIcons.KeyFor(entry.Name, entry.IsDirectory, isExpanded: false);
        _openIconKey = entry.IsDirectory ? FileIcons.KeyFor(entry.Name, isDirectory: true, isExpanded: true) : _closedIconKey;
    }

    public string Name { get; }
    public string FullPath { get; }

    /// <summary>Slash-separated path relative to the project root, cached so git decoration avoids per-node path computation.</summary>
    public string? RelativePath { get; }

    public bool IsDirectory { get; }
    public bool IsHidden { get; }

    private static readonly BatchObservableCollection<FileNodeViewModel> EmptyChildren = [];
    private BatchObservableCollection<FileNodeViewModel>? _children;

    public ObservableCollection<FileNodeViewModel> Children =>
        IsDirectory ? (_children ??= []) : EmptyChildren;

    internal BatchObservableCollection<FileNodeViewModel> BatchChildren =>
        IsDirectory ? (_children ??= []) : EmptyChildren;

    /// <summary>True once the folder's children were read. The tree draws the arrow of a folder that has children.</summary>
    public bool IsLoaded { get; internal set; }

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    /// <summary>The git status of the file; null for a clean file or any folder.</summary>
    [ObservableProperty] public partial GitChangeKind? GitKind { get; private set; }

    /// <summary>A folder that contains changed files.</summary>
    [ObservableProperty] public partial bool HasChangedDescendant { get; private set; }

    /// <summary>VS Code style colored icon key resolved once at creation; switches to open folder variant when expanded.</summary>
    public string IconKey => IsDirectory && IsExpanded ? _openIconKey : _closedIconKey;

    public LucideIconKind IconKind => FileIcons.For(Name, IsDirectory, IsDirectory && IsExpanded);

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IconKey));
        OnPropertyChanged(nameof(IconKind));
    }

    internal void ApplyGit(GitChangeKind? kind, bool hasChangedDescendant)
    {
        GitKind = kind;
        HasChangedDescendant = hasChangedDescendant;
    }
}
