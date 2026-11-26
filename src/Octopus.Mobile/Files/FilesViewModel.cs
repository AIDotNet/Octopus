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
public sealed partial class FilesViewModel : ObservableObject, IDisposable
{
    /// <summary>Upper bound on entries kept per folder; virtualization keeps thousands of rows smooth on a phone.</summary>
    public const int MaxEntries = 5000;
    private const int SearchThreshold = 12;

    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<FileRowViewModel> _allEntries = [];
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
    [ObservableProperty] public partial string SearchQuery { get; set; } = "";
    /// <summary>Atomically swapped row list bound to the virtualized <c>ListBox</c> (avoids N per-item collection events).</summary>
    [ObservableProperty] public partial IReadOnlyList<FileRowViewModel> Entries { get; private set; } = [];

    public string Title => Crumbs[0].Name;
    /// <summary>The folder started in, then each one walked into; each opens that folder again.</summary>
    public ObservableCollection<FileCrumbViewModel> Crumbs { get; } = [];

    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public bool HasEntries => !IsLoading && !HasError && Entries.Count > 0;
    public bool IsEmpty => !IsLoading && !HasError && Entries.Count == 0;
    public bool IsCapped => CappedText is not null;
    public bool ShowSearch => !IsLoading && !HasError && (_allEntries.Count > SearchThreshold || !string.IsNullOrEmpty(SearchQuery));

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
            var entries = await _workspace.ListFilesAsync(Project, folder, _lifetime.Token);
            if (load != _loads || _lifetime.IsCancellationRequested) return;
            var count = Math.Min(entries.Count, MaxEntries);
            var rows = new FileRowViewModel[count];
            for (var i = 0; i < count; i++) rows[i] = new FileRowViewModel(entries[i], Open);
            _allEntries = rows;
            ApplyFilter();
            CappedText = entries.Count > MaxEntries ? string.Format(MobileLangResources.Get(MobileLangResourceKind.FilesCapped), MaxEntries) : null;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (IOException e)
        {
            if (load != _loads) return;
            _allEntries = [];
            Entries = [];
            CappedText = null;
            ErrorText = e.Message;
        }
        finally
        {
            if (load == _loads && !_lifetime.IsCancellationRequested)
            {
                IsLoading = false;
                Refresh();
            }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    [RelayCommand]
    private void Back() => _navigator.Back();

    /// <summary>A folder is walked into; a file opens, with where it is (the parts walked) under its name.</summary>
    private void Open(FileRowViewModel row)
    {
        if (row.IsDirectory)
        {
            Crumbs.Add(new FileCrumbViewModel(row.Name, row.Path, OpenCrumb));
            SearchQuery = "";
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
        SearchQuery = "";
        Folder = crumb.Path;
        _ = LoadAsync();
    }

    partial void OnSearchQueryChanged(string value)
    {
        ApplyFilter();
        Refresh();
    }

    private void ApplyFilter()
    {
        var query = SearchQuery.Trim();
        if (query.Length == 0)
        {
            Entries = _allEntries;
            return;
        }
        var filtered = new List<FileRowViewModel>();
        foreach (var row in _allEntries)
        {
            if (row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) filtered.Add(row);
        }
        Entries = filtered;
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsCapped));
        OnPropertyChanged(nameof(ShowSearch));
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
