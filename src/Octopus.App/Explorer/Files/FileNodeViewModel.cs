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
    public FileNodeViewModel(FileEntry entry)
    {
        Name = entry.Name;
        FullPath = entry.FullPath;
        IsDirectory = entry.IsDirectory;
        IsHidden = entry.IsHidden;
    }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public bool IsHidden { get; }

    public ObservableCollection<FileNodeViewModel> Children { get; } = [];

    /// <summary>True once the folder's children were read. The tree draws the arrow of a folder that has children.</summary>
    public bool IsLoaded { get; internal set; }

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    /// <summary>The git status of the file; null for a clean file or any folder.</summary>
    [ObservableProperty] public partial GitChangeKind? GitKind { get; private set; }

    /// <summary>A folder that contains changed files.</summary>
    [ObservableProperty] public partial bool HasChangedDescendant { get; private set; }

    public LucideIconKind IconKind => FileIcons.For(Name, IsDirectory, IsDirectory && IsExpanded);

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(IconKind));

    internal void ApplyGit(GitChangeKind? kind, bool hasChangedDescendant)
    {
        GitKind = kind;
        HasChangedDescendant = hasChangedDescendant;
    }
}
