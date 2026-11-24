using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Hosts;

/// <summary>
/// Picks a folder of a host and makes it a project there (design/ui/移动端功能扩展方案.html, ③ "目录浏览"). It starts at the
/// host's home; the path is shown as tappable parts, home and the drives sit above the list, and a new empty folder
/// can be made on the way.
/// </summary>
public sealed partial class FolderPickerViewModel : ObservableObject
{
    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;
    private readonly Action<RemoteProject>? _created;
    private int _loads;

    /// <param name="created">Runs once the project exists on the host (the page has left by then).</param>
    public FolderPickerViewModel(RemoteHost host, IRemoteWorkspace workspace, INavigator navigator, Action<RemoteProject>? created = null)
    {
        Host = host;
        _workspace = workspace;
        _navigator = navigator;
        _created = created;
        Path = "";
        NewFolderName = "";
    }

    public RemoteHost Host { get; }

    /// <summary>The folder shown; empty while on the list of drives.</summary>
    [ObservableProperty] public partial string Path { get; private set; }
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string? ErrorText { get; private set; }
    [ObservableProperty] public partial bool IsNewFolderOpen { get; set; }
    [ObservableProperty] public partial string NewFolderName { get; set; }

    public ObservableCollection<FolderRowViewModel> Folders { get; } = [];
    public ObservableCollection<FolderRowViewModel> Roots { get; } = [];
    /// <summary>The parts of <see cref="Path"/>, each opening that folder.</summary>
    public ObservableCollection<FolderRowViewModel> Crumbs { get; } = [];

    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public bool IsEmpty => !IsLoading && !HasError && Folders.Count == 0;
    /// <summary>A real folder is shown (not the list of drives): it can become a project and hold a new folder.</summary>
    public bool CanChoose => Path.Length > 0 && !IsLoading && !HasError;
    public string FolderName => Crumbs.Count > 0 ? Crumbs[^1].Name : Path;
    public string ChooseText => string.Format(MobileLangResources.Get(MobileLangResourceKind.FolderUseAsProject), FolderName);

    /// <summary>Reads the folder (null: the host's home; empty: its drives).</summary>
    public async Task LoadAsync(string? path = null)
    {
        var load = ++_loads;
        IsLoading = true;
        ErrorText = null;
        Refresh();
        try
        {
            var listing = await _workspace.BrowseFolderAsync(Host, path);
            if (load != _loads) return;
            Path = listing.Path;
            Replace(Folders, listing.Folders.Select(entry => new FolderRowViewModel(entry.Name, entry.Path, entry.Kind, entry.IsProject, Open)));
            var roots = listing.Roots.Select(entry => new FolderRowViewModel(RootName(entry, listing.Home), entry.Path, entry.Kind, false, Open)).ToList();
            // On Windows the drives are a list of their own, one step above each drive.
            if (listing.Separator == "\\" && roots.All(root => root.Path.Length > 0))
                roots.Add(new FolderRowViewModel(MobileLangResources.Get(MobileLangResourceKind.FolderDrives), "", "drive", false, Open));
            Replace(Roots, roots);
            Replace(Crumbs, CrumbsOf(listing));
        }
        catch (IOException e)
        {
            if (load == _loads) ErrorText = e.Message;
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

    [RelayCommand]
    private void Open(string? path) => _ = LoadAsync(path ?? "");

    [RelayCommand]
    private void BeginNewFolder()
    {
        if (!CanChoose) return;
        NewFolderName = "";
        IsNewFolderOpen = true;
    }

    [RelayCommand]
    private async Task CreateFolderAsync()
    {
        if (!CanChoose || string.IsNullOrWhiteSpace(NewFolderName) || IsBusy) return;
        IsBusy = true;
        try
        {
            var created = await _workspace.CreateFolderAsync(Host, Path, NewFolderName);
            IsNewFolderOpen = false;
            await LoadAsync(created);
        }
        catch (IOException e)
        {
            IsNewFolderOpen = false;
            ErrorText = e.Message;
            Refresh();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Makes the folder shown a project on the host and goes back to the host's page.</summary>
    [RelayCommand]
    private async Task ChooseAsync()
    {
        if (!CanChoose || IsBusy) return;
        IsBusy = true;
        try
        {
            var project = await _workspace.CreateProjectAsync(Host, Path);
            _navigator.Back();
            _created?.Invoke(project);
        }
        catch (IOException e)
        {
            ErrorText = e.Message;
            Refresh();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanChoose));
        OnPropertyChanged(nameof(FolderName));
        OnPropertyChanged(nameof(ChooseText));
    }

    // "/Users/ada/code" as /, Users, ada, code; "C:\src" as C:, src; the list of drives has none.
    private List<FolderRowViewModel> CrumbsOf(RemoteFolderListing listing)
    {
        var crumbs = new List<FolderRowViewModel>();
        if (listing.Path.Length == 0) return crumbs;
        var separator = listing.Separator.Length > 0 ? listing.Separator[0] : '/';
        var parts = listing.Path.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        var unix = listing.Path.StartsWith(separator);
        var built = unix ? separator.ToString() : "";
        if (unix) crumbs.Add(new FolderRowViewModel(separator.ToString(), built, "drive", false, Open));
        for (var i = 0; i < parts.Length; i++)
        {
            built = i == 0 && !unix ? parts[0] + separator : built.EndsWith(separator) ? built + parts[i] : built + separator + parts[i];
            crumbs.Add(new FolderRowViewModel(parts[i], built, "folder", false, Open));
        }
        return crumbs;
    }

    private static string RootName(RemoteFolderEntry root, string home) => root.Kind switch
    {
        "home" => MobileLangResources.Get(MobileLangResourceKind.FolderHome),
        _ => root.Name.Length > 0 ? root.Name : root.Path,
    };

    private static void Replace(ObservableCollection<FolderRowViewModel> target, IEnumerable<FolderRowViewModel> rows)
    {
        target.Clear();
        foreach (var row in rows) target.Add(row);
    }

    partial void OnErrorTextChanged(string? value) => Refresh();
}

/// <summary>A folder in the list, a part of the path, or a shortcut above the list.</summary>
public sealed partial class FolderRowViewModel(string name, string path, string kind, bool isProject, Action<string?> open) : ObservableObject
{
    public string Name { get; } = name;
    public string Path { get; } = path;
    public string Kind { get; } = kind;
    public bool IsProject { get; } = isProject;
    public string? Subtitle => IsProject ? MobileLangResources.Get(MobileLangResourceKind.FolderIsProject) : null;

    [RelayCommand]
    private void Open() => open(Path);
}
