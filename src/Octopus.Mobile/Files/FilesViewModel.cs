using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Common;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;
using TKCode.Arc;

namespace Octopus.Mobile.Files;

/// <summary>
/// The files and folders of a project's folder on the host, one folder at a time (design/ui/移动端功能扩展方案.html, ⑤).
/// The folders walked into show as tappable parts above the list; a file opens read-only.
/// </summary>
public sealed partial class FilesViewModel : ObservableObject
{
    /// <summary>More entries than this are not listed: a phone list of thousands (node_modules) only crawls.</summary>
    public const int MaxEntries = 500;

    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;
    private int _loads;

    /// <param name="root">The folder to start in: the project's own, a branch workspace's, or one inside them.</param>
    public FilesViewModel(RemoteProject project, string root, IRemoteWorkspace workspace, INavigator navigator)
    {
        Project = project;
        _workspace = workspace;
        _navigator = navigator;
        Folder = root;
        Crumbs.Add(new FileCrumbViewModel(root == project.Path ? project.Name : NameOf(root), root, OpenCrumb));
    }

    public RemoteProject Project { get; }

    /// <summary>The folder shown, as the host names it.</summary>
    [ObservableProperty] public partial string Folder { get; private set; }
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial string? ErrorText { get; private set; }
    [ObservableProperty] public partial string? CappedText { get; private set; }

    public string Title => Crumbs[0].Name;
    public ObservableCollection<FileRowViewModel> Entries { get; } = [];
    /// <summary>The folder started in, then each one walked into; each opens that folder again.</summary>
    public ObservableCollection<FileCrumbViewModel> Crumbs { get; } = [];

    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public bool HasEntries => !IsLoading && !HasError && Entries.Count > 0;
    public bool IsEmpty => !IsLoading && !HasError && Entries.Count == 0;
    public bool IsCapped => CappedText is not null;

    /// <summary>Reads <see cref="Folder"/> again.</summary>
    public async Task LoadAsync()
    {
        var load = ++_loads;
        var folder = Folder;
        IsLoading = true;
        ErrorText = null;
        Refresh();
        try
        {
            var entries = await _workspace.ListFilesAsync(Project, folder);
            if (load != _loads) return;
            Entries.Clear();
            foreach (var entry in entries.Take(MaxEntries)) Entries.Add(new FileRowViewModel(entry, Open));
            CappedText = entries.Count > MaxEntries ? string.Format(MobileLangResources.Get(MobileLangResourceKind.FilesCapped), MaxEntries) : null;
        }
        catch (IOException e)
        {
            if (load != _loads) return;
            Entries.Clear();
            CappedText = null;
            ErrorText = e.Message;
        }
        finally
        {
            if (load == _loads)
            {
                IsLoading = false;
                Refresh();
            }
        }
    }

    [RelayCommand]
    private void Back() => _navigator.Back();

    /// <summary>A folder is walked into; a file opens, with where it is (the parts walked) under its name.</summary>
    private void Open(FileRowViewModel row)
    {
        if (row.IsDirectory)
        {
            Crumbs.Add(new FileCrumbViewModel(row.Name, row.Path, OpenCrumb));
            Folder = row.Path;
            _ = LoadAsync();
        }
        else
        {
            // "src/Auth" as in the design; the folder started in when the file lies right there.
            var location = Crumbs.Count > 1 ? string.Join("/", Crumbs.Skip(1).Select(crumb => crumb.Name)) : Crumbs[0].Name;
            _navigator.ShowFile(Project, row.Path, location);
        }
    }

    private void OpenCrumb(FileCrumbViewModel crumb)
    {
        var index = Crumbs.IndexOf(crumb);
        if (index < 0) return;
        while (Crumbs.Count > index + 1) Crumbs.RemoveAt(Crumbs.Count - 1);
        Folder = crumb.Path;
        _ = LoadAsync();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsCapped));
    }

    /// <summary>The last part of a host path, with either separator.</summary>
    internal static string NameOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        return cut >= 0 && cut < trimmed.Length - 1 ? trimmed[(cut + 1)..] : trimmed.Length > 0 ? trimmed : path;
    }
}

/// <summary>A file or folder in the list.</summary>
public sealed partial class FileRowViewModel(RemoteFileEntry entry, Action<FileRowViewModel> open) : ObservableObject
{
    public string Name => entry.Name;
    public string Path => entry.Path;
    public bool IsDirectory => entry.IsDirectory;
    public Geometry Icon => entry.IsDirectory
        ? ArcIcons.Folder
        : MobileFileLinks.IsImageExtension(entry.Name)
            ? ArcIcons.Image
            : ArcIcons.FileText;

    [RelayCommand]
    private void Open() => open(this);
}

/// <summary>A folder above the list: the one started in, or one walked into.</summary>
public sealed partial class FileCrumbViewModel(string name, string path, Action<FileCrumbViewModel> open) : ObservableObject
{
    public string Name { get; } = name;
    public string Path { get; } = path;

    [RelayCommand]
    private void Open() => open(this);
}
