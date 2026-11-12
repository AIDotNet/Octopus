using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.App.Explorer.Files;
using Octopus.App.Files;
using Octopus.App.Explorer.Git;
using Octopus.App.Explorer.Search;
using Octopus.App.Localization;
using Octopus.FileSystem;
using Octopus.Git;
using Octopus.Storage;

namespace Octopus.App.Explorer;

/// <summary>The right panel: which tab shows, whether the folder is in a git repository, and the parts it is made of.</summary>
public sealed partial class ExplorerPanelViewModel : ObservableObject, IDisposable
{
    private readonly IFileTreeService _tree;
    private readonly IGitService _git;
    private readonly ISettingsStore _settings;
    private readonly UiContext _ui = new();
    private IDisposable? _watch;
    private IDisposable? _changeWatch;
    private IDisposable? _gitWatch;
    private int _version;

    public ExplorerPanelViewModel(
        FileTreeViewModel files,
        FileSearchViewModel search,
        GitPanelViewModel gitPanel,
        IFileTreeService tree,
        IGitService git,
        ISettingsStore settings)
    {
        Files = files;
        Search = search;
        Git = gitPanel;
        _tree = tree;
        _git = git;
        _settings = settings;
        Tab = settings.Current.RightPanel.Tab == "git" ? ExplorerTab.Git : ExplorerTab.Files;

        files.OpenRequested += (_, path) => FileOpenRequested?.Invoke(this, path);
        search.OpenRequested += (_, path) => FileOpenRequested?.Invoke(this, path);
        gitPanel.OpenRequested += (_, path) => FileOpenRequested?.Invoke(this, path);
        gitPanel.DiffRequested += (_, request) => DiffRequested?.Invoke(this, request);
        gitPanel.StatusChanged += OnGitStatusChanged;
    }

    /// <summary>How long file changes are collected before the git status is read again.</summary>
    public TimeSpan GitRefreshDelay { get; set; } = TimeSpan.FromMilliseconds(800);

    public FileTreeViewModel Files { get; }
    public FileSearchViewModel Search { get; }
    public GitPanelViewModel Git { get; }

    [ObservableProperty] public partial string? ProjectPath { get; private set; }
    [ObservableProperty] public partial string ProjectName { get; set; } = "";
    [ObservableProperty] public partial bool IsGitRepository { get; private set; }

    /// <summary>The number of changed files, for the badge on the Git tab.</summary>
    [ObservableProperty] public partial int ChangeCount { get; private set; }

    /// <summary>The tab the person chose; it falls back to files when the folder is not in a repository.</summary>
    [ObservableProperty] public partial ExplorerTab Tab { get; private set; }

    public bool HasProject => ProjectPath is not null;
    public bool HasNoProject => ProjectPath is null;
    public bool IsFilesTab => HasProject && !IsGitTab;
    public bool IsGitTab => HasProject && IsGitRepository && Tab == ExplorerTab.Git;
    public bool HasChanges => ChangeCount > 0;

    /// <summary>The key of the tab showing ("files" or "git"), for the tab switch; setting it chooses that tab.</summary>
    public string TabKey
    {
        get => IsGitTab ? "git" : "files";
        set
        {
            if (value == "git") ShowGit();
            else if (value == "files") ShowFiles();
        }
    }

    /// <summary>The tooltip of the Git tab: its name and, when there are changes, how many files changed.</summary>
    public string GitTabTooltip => HasChanges
        ? $"{AppLangResources.Get(AppLangResourceKind.ExplorerTabGit)} ({ChangeCount})"
        : AppLangResources.Get(AppLangResourceKind.ExplorerTabGit);

    /// <summary>Raised with the absolute path of a file the person wants opened.</summary>
    public event EventHandler<string>? FileOpenRequested;

    /// <summary>Raised with the change whose read-only diff the person wants to see.</summary>
    public event EventHandler<DiffTabRequest>? DiffRequested;

    partial void OnProjectPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(HasNoProject));
        NotifyTabs();
    }

    partial void OnIsGitRepositoryChanged(bool value) => NotifyTabs();

    partial void OnTabChanged(ExplorerTab value) => NotifyTabs();

    partial void OnChangeCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(GitTabTooltip));
    }

    private void NotifyTabs()
    {
        OnPropertyChanged(nameof(IsFilesTab));
        OnPropertyChanged(nameof(IsGitTab));
        OnPropertyChanged(nameof(TabKey));
    }

    [RelayCommand]
    public void ShowFiles() => SelectTab(ExplorerTab.Files);

    [RelayCommand]
    public void ShowGit()
    {
        if (!IsGitRepository) return;
        SelectTab(ExplorerTab.Git);
    }

    private void SelectTab(ExplorerTab tab)
    {
        if (Tab == tab) return;
        Tab = tab;
        _settings.Update(s => s.RightPanel.Tab = tab == ExplorerTab.Git ? "git" : "files");
        if (tab == ExplorerTab.Git) _ = Git.RefreshAsync();
    }

    /// <summary>Points the panel at a project folder (null: none): loads the tree, finds out about git and starts watching.</summary>
    public async Task SetProjectAsync(string? path, string? name)
    {
        var version = ++_version;
        StopWatching();

        var folder = path is null || !Directory.Exists(path) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        ProjectName = name ?? (folder is null ? "" : PathText.NameOf(folder));
        IsGitRepository = false;
        ChangeCount = 0;
        Git.SetRepository(null);
        Files.Load(folder);
        Search.SetRoot(folder);
        ProjectPath = folder;
        if (folder is null) return;

        _watch = _tree.Watch(folder, OnDirectoryChanged);

        var root = await _git.FindRepositoryRootAsync(folder);
        if (version != _version || root is null) return;
        Git.SetRepository(root);
        IsGitRepository = true;
        // Any change in the folder (a file saved, `git add`, `git commit`, `git checkout` in a terminal) may change the status.
        _changeWatch = _tree.WatchChanges(folder, OnAnyChange, GitRefreshDelay);
        // The repository may be above the project folder: its .git is then outside the watch above, so the real git
        // directory (a worktree's .git is a file) is watched for HEAD, index and refs on its own.
        if (await _git.GetGitDirectoriesAsync(folder) is { } directories && version == _version)
        {
            _gitWatch = _tree.WatchGitDirectory(directories.GitDirectory, directories.CommonDirectory, OnAnyChange, GitRefreshDelay);
        }
        await Git.RefreshAsync(includeLog: IsGitTab);
    }

    /// <summary>The window came back to the front: files may have changed outside the app.</summary>
    public void NotifyActivated()
    {
        if (IsGitRepository) _ = Git.RefreshAsync(includeLog: IsGitTab);
    }

    /// <summary>The refresh button of the active tab.</summary>
    [RelayCommand]
    public void Refresh()
    {
        if (IsGitTab) _ = Git.RefreshAsync();
        else Files.Reload();
    }

    private void OnDirectoryChanged(string directory)
    {
        // Called on a background thread by the watcher.
        _ui.Post(() =>
        {
            Files.RefreshDirectory(directory);
        });
    }

    private void OnAnyChange()
    {
        // Called on a background thread by the watcher, already debounced.
        _ui.Post(() =>
        {
            if (IsGitRepository) _ = Git.RefreshAsync(includeLog: IsGitTab);
        });
    }

    private void StopWatching()
    {
        _watch?.Dispose();
        _watch = null;
        _changeWatch?.Dispose();
        _changeWatch = null;
        _gitWatch?.Dispose();
        _gitWatch = null;
    }

    private void OnGitStatusChanged(object? sender, GitStatus? status)
    {
        ChangeCount = status?.Changes.Select(c => c.Path).Distinct().Count() ?? 0;
        if (ProjectPath is { } path) Files.ApplyGitStatus(status, RealPath.Resolve(path));
    }

    public void Dispose() => StopWatching();
}
