using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.App.Explorer.Files;
using Octopus.App.Files;
using Octopus.App.Explorer.Git;
using Octopus.App.Explorer.Search;
using Octopus.App.Explorer.Sessions;
using Octopus.App.Explorer.Tunnels;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.FileSystem;
using Octopus.Git;
using Octopus.Ssh;
using Octopus.Storage;

namespace Octopus.App.Explorer;

/// <summary>The right panel: which tab shows, whether the folder is in a git repository, and the parts it is made of.</summary>
public sealed partial class ExplorerPanelViewModel : ObservableObject, IDisposable
{
    private readonly IFileTreeService _tree;
    private readonly IGitService _git;
    private readonly ISettingsStore _settings;
    private readonly RemoteFiles _remote;
    private readonly UiContext _ui = new();
    private SshFileTree? _remoteTree;
    private IDisposable? _watch;
    private IDisposable? _changeWatch;
    private IDisposable? _gitWatch;
    private int _version;
    private bool _isActive = true;
    /// <summary>The project the shell asked for while the panel was closed: it is read when the panel opens.</summary>
    private (string? Path, string? Name, string? SshHost)? _pendingProject;
    /// <summary>The local folder's watches were stopped while the panel was closed: on opening, they restart and the panel catches up.</summary>
    private bool _paused;
    private GitDirectories? _gitDirectories;
    /// <summary>The git lookup of the current folder finished (whether or not it is in a repository).</summary>
    private bool _gitLookedUp;

    public ExplorerPanelViewModel(
        FileTreeViewModel files,
        FileSearchViewModel search,
        GitPanelViewModel gitPanel,
        TunnelsPanelViewModel tunnels,
        SessionsPanelViewModel sessions,
        IFileTreeService tree,
        IGitService git,
        ISettingsStore settings,
        RemoteFiles remote)
    {
        _remote = remote;
        Files = files;
        Search = search;
        Git = gitPanel;
        Tunnels = tunnels;
        Sessions = sessions;
        _tree = tree;
        _git = git;
        _settings = settings;
        Tab = settings.Current.RightPanel.Tab switch
        {
            "git" => ExplorerTab.Git,
            "tunnels" => ExplorerTab.Tunnels,
            "sessions" => ExplorerTab.Sessions,
            _ => ExplorerTab.Files,
        };

        files.OpenRequested += (_, path) => FileOpenRequested?.Invoke(this, path);
        search.OpenRequested += (_, path) => FileOpenRequested?.Invoke(this, path);
        gitPanel.OpenRequested += (_, path) => FileOpenRequested?.Invoke(this, path);
        gitPanel.DiffRequested += (_, request) => DiffRequested?.Invoke(this, request);
        gitPanel.StatusChanged += OnGitStatusChanged;
        tunnels.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TunnelsPanelViewModel.RunningCount)) NotifyTunnelCount();
        };
    }

    /// <summary>How long file changes are collected before the git status is read again.</summary>
    public TimeSpan GitRefreshDelay { get; set; } = TimeSpan.FromMilliseconds(800);

    public FileTreeViewModel Files { get; }
    public FileSearchViewModel Search { get; }
    public GitPanelViewModel Git { get; }
    public TunnelsPanelViewModel Tunnels { get; }
    public SessionsPanelViewModel Sessions { get; }

    [ObservableProperty] public partial string? ProjectPath { get; private set; }

    /// <summary>The message of the last failed read of an SSH host's folders, or null.</summary>
    [ObservableProperty] public partial string? RemoteStatus { get; private set; }

    public bool HasRemoteStatus => RemoteStatus is not null;

    /// <summary>The host is being reached or its folders are being read: the panel shows a loading line.</summary>
    [ObservableProperty] public partial bool IsRemoteBusy { get; private set; }

    /// <summary>What the loading line says: reaching the host, or reading its folders.</summary>
    public string RemoteBusyText => AppLangResources.Get(_resolving ? AppLangResourceKind.ExplorerRemoteConnecting : AppLangResourceKind.ExplorerRemoteReading);

    private bool _resolving;
    private bool _treeBusy;

    public string? RemoteStatusText => RemoteStatus is null ? null : AppLangResources.Format(AppLangResourceKind.ExplorerRemoteStatus, RemoteStatus);
    [ObservableProperty] public partial string ProjectName { get; set; } = "";
    [ObservableProperty] public partial bool IsGitRepository { get; private set; }

    /// <summary>The project is on an SSH host: the panel then has a tunnels tab (and no git, no search).</summary>
    [ObservableProperty] public partial bool IsSshProject { get; private set; }

    /// <summary>The number of changed files, for the badge on the Git tab.</summary>
    [ObservableProperty] public partial int ChangeCount { get; private set; }

    /// <summary>The tab the person chose; it falls back to files when the folder is not in a repository.</summary>
    [ObservableProperty] public partial ExplorerTab Tab { get; private set; }

    public bool HasProject => ProjectPath is not null;
    public bool HasNoProject => ProjectPath is null;
    public bool IsFilesTab => HasProject && !IsGitTab && !IsTunnelsTab && !IsSessionsTab;
    public bool IsGitTab => HasProject && IsGitRepository && Tab == ExplorerTab.Git;
    public bool IsTunnelsTab => HasProject && IsSshProject && Tab == ExplorerTab.Tunnels;
    /// <summary>Sessions are read from this computer: a project on an SSH host has none here.</summary>
    public bool IsSessionsTab => HasProject && !IsSshProject && Tab == ExplorerTab.Sessions;
    public bool HasChanges => ChangeCount > 0;

    /// <summary>The numbers next to the tab icons: changed files, running tunnels.</summary>
    public string ChangeCountText => ChangeCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
    public string RunningTunnelsText => Tunnels.RunningCount.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>The key of the tab showing ("files", "git", "tunnels" or "sessions"), for the tab switch; setting it chooses that tab.</summary>
    public string TabKey
    {
        get => IsGitTab ? "git" : IsTunnelsTab ? "tunnels" : IsSessionsTab ? "sessions" : "files";
        set
        {
            if (value == "git") ShowGit();
            else if (value == "tunnels") ShowTunnels();
            else if (value == "sessions") ShowSessions();
            else if (value == "files") ShowFiles();
        }
    }

    /// <summary>Some tunnel of the project runs: the tunnels tab shows a dot.</summary>
    public bool HasRunningTunnels => Tunnels.RunningCount > 0;

    /// <summary>The tooltip of the tunnels tab: its name and, when some run, how many.</summary>
    public string TunnelsTabTooltip => HasRunningTunnels
        ? AppLangResources.Format(AppLangResourceKind.ExplorerTabTunnelsRunning, Tunnels.RunningCount)
        : AppLangResources.Get(AppLangResourceKind.ExplorerTabTunnels);

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

    partial void OnIsSshProjectChanged(bool value) => NotifyTabs();

    partial void OnRemoteStatusChanged(string? value)
    {
        OnPropertyChanged(nameof(HasRemoteStatus));
        OnPropertyChanged(nameof(RemoteStatusText));
    }

    partial void OnTabChanged(ExplorerTab value) => NotifyTabs();

    partial void OnChangeCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(ChangeCountText));
        OnPropertyChanged(nameof(GitTabTooltip));
    }

    private void NotifyTabs()
    {
        OnPropertyChanged(nameof(IsFilesTab));
        OnPropertyChanged(nameof(IsGitTab));
        OnPropertyChanged(nameof(IsTunnelsTab));
        OnPropertyChanged(nameof(IsSessionsTab));
        OnPropertyChanged(nameof(TabKey));
        Sessions.IsActive = _isActive && IsSessionsTab;
    }

    private void NotifyTunnelCount()
    {
        OnPropertyChanged(nameof(HasRunningTunnels));
        OnPropertyChanged(nameof(RunningTunnelsText));
        OnPropertyChanged(nameof(TunnelsTabTooltip));
    }

    [RelayCommand]
    public void ShowFiles() => SelectTab(ExplorerTab.Files);

    [RelayCommand]
    public void ShowGit()
    {
        if (!IsGitRepository) return;
        SelectTab(ExplorerTab.Git);
    }

    [RelayCommand]
    public void ShowTunnels()
    {
        if (!IsSshProject) return;
        SelectTab(ExplorerTab.Tunnels);
    }

    [RelayCommand]
    public void ShowSessions()
    {
        if (IsSshProject) return;
        SelectTab(ExplorerTab.Sessions);
    }

    private void SelectTab(ExplorerTab tab)
    {
        if (Tab == tab) return;
        Tab = tab;
        _settings.Update(s => s.RightPanel.Tab = tab switch
        {
            ExplorerTab.Git => "git",
            ExplorerTab.Tunnels => "tunnels",
            ExplorerTab.Sessions => "sessions",
            _ => "files",
        });
        if (tab == ExplorerTab.Git) _ = Git.RefreshAsync();
    }

    /// <summary>
    /// Whether the panel is on screen. A closed panel does no work: nothing it shows is used elsewhere, yet its tree,
    /// watches and git reads would cost on every project switch, every file change and every return to the window. The
    /// project asked for meanwhile is read when the panel opens; the watches of the project it shows stop while it is
    /// closed and it reads again what may have changed when it opens.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            Sessions.IsActive = value && IsSessionsTab;
            if (value) _ = ResumeAsync();
            else Pause();
        }
    }

    private void Pause()
    {
        // An SSH tree only reads when asked, so it is left as it is; a local folder's watches stop.
        if (ProjectPath is null || IsSshProject || _pendingProject is not null) return;
        _version++; // a git lookup still running for this folder must not start watches now
        DisposeLocalWatches();
        _paused = true;
    }

    private async Task ResumeAsync()
    {
        if (_pendingProject is { } pending)
        {
            await SetProjectAsync(pending.Path, pending.Name, pending.SshHost);
            return;
        }
        if (!_paused || ProjectPath is not { } folder) return;
        _paused = false;
        var version = ++_version;
        if (!_gitLookedUp)
        {
            // The git lookup had not finished when the panel closed: the folder is read again from the start.
            await SetProjectAsync(folder, ProjectName);
            return;
        }
        WatchLocal(folder);
        Files.Reload();
        if (version == _version) await Git.RefreshAsync(includeLog: IsGitTab);
    }

    /// <summary>
    /// Points the panel at a project folder (null: none): loads the tree, finds out about git and starts watching. A folder
    /// on an SSH host (<paramref name="sshHost"/>) is read through the host: there is no git and no search for it here.
    /// While the panel is closed (<see cref="IsActive"/>) this only remembers the folder.
    /// </summary>
    public async Task SetProjectAsync(string? path, string? name, string? sshHost = null)
    {
        var version = ++_version;
        StopWatching();
        _paused = false;
        if (!_isActive)
        {
            _pendingProject = (path, name, sshHost);
            return;
        }
        _pendingProject = null;
        _gitDirectories = null;
        _gitLookedUp = false;
        IsSshProject = path is not null && sshHost is not null;
        Sessions.SetProject(sshHost is null && path is not null && Directory.Exists(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null);
        if (path is not null && sshHost is not null)
        {
            await SetRemoteProjectAsync(path, name, sshHost, version);
            return;
        }
        RemoteStatus = null;

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
        if (version != _version) return;
        if (root is null)
        {
            _gitLookedUp = true;
            return;
        }
        var directories = await _git.GetGitDirectoriesAsync(folder);
        if (version != _version) return;
        _gitLookedUp = true;
        Git.SetRepository(root);
        IsGitRepository = true;
        _gitDirectories = directories;
        WatchGit(folder);
        await Git.RefreshAsync(includeLog: IsGitTab);
    }

    private void WatchLocal(string folder)
    {
        _watch = _tree.Watch(folder, OnDirectoryChanged);
        if (IsGitRepository) WatchGit(folder);
    }

    private void WatchGit(string folder)
    {
        // Any change in the folder (a file saved, `git add`, `git commit`, `git checkout` in a terminal) may change the status.
        _changeWatch = _tree.WatchChanges(folder, OnAnyChange, GitRefreshDelay);
        // The repository may be above the project folder (or the folder is a worktree, whose .git is a file): the real
        // git directory is then outside the watch above and is watched for HEAD, index and refs on its own. Inside the
        // folder it is not watched twice: each git command would otherwise be read twice.
        if (_gitDirectories is { } directories && (IsOutside(folder, directories.GitDirectory) || IsOutside(folder, directories.CommonDirectory)))
        {
            _gitWatch = _tree.WatchGitDirectory(directories.GitDirectory, directories.CommonDirectory, OnAnyChange, GitRefreshDelay);
        }
    }

    private static bool IsOutside(string folder, string directory) =>
        RealPath.Relative(RealPath.Resolve(folder), RealPath.Resolve(directory)) is null;

    /// <summary>The window came back to the front: files may have changed outside the app.</summary>
    public void NotifyActivated()
    {
        if (_isActive && IsGitRepository) _ = Git.RefreshAsync(includeLog: IsGitTab);
    }

    /// <summary>The refresh button of the active tab.</summary>
    [RelayCommand]
    public void Refresh()
    {
        if (IsGitTab) _ = Git.RefreshAsync();
        else if (IsSessionsTab) _ = Sessions.RefreshAsync();
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

    /// <summary>
    /// The folder is found on the host first (its absolute path, with "~" expanded there); then the tree reads the host.
    /// A host that cannot be reached is said in the panel, and the tree stays empty until it can be read.
    /// </summary>
    private async Task SetRemoteProjectAsync(string path, string? name, string host, int version)
    {
        var tree = _remote.TreeFor(host);
        ProjectName = name ?? PathText.NameOf(path);
        IsGitRepository = false;
        ChangeCount = 0;
        Git.SetRepository(null);
        Files.Load(null);
        Search.SetRoot(null);
        ProjectPath = path;
        _remoteTree = tree;
        tree.StatusChanged += OnRemoteStatus;
        tree.BusyChanged += OnRemoteBusy;
        _resolving = true;
        UpdateBusy();

        string root;
        try
        {
            root = await tree.ResolveAsync(path);
        }
        catch (SshCommandException e)
        {
            if (version == _version) RemoteStatus = e.Message;
            return;
        }
        finally
        {
            if (version == _version) _resolving = false;
            UpdateBusy();
        }
        if (version != _version) return;
        Files.Load(root, tree);
        _watch = tree.Watch(root, OnDirectoryChanged);
        RemoteStatus = tree.Status;
    }

    private void OnRemoteStatus(object? sender, string? message)
    {
        // Called on a background thread by the tree.
        _ui.Post(() => RemoteStatus = message);
    }

    private void OnRemoteBusy(object? sender, bool busy)
    {
        // Called on a background thread by the tree.
        _ui.Post(() =>
        {
            _treeBusy = busy;
            UpdateBusy();
        });
    }

    private void UpdateBusy()
    {
        IsRemoteBusy = _resolving || _treeBusy;
        OnPropertyChanged(nameof(RemoteBusyText));
    }

    private void StopWatching()
    {
        if (_remoteTree is not null)
        {
            _remoteTree.StatusChanged -= OnRemoteStatus;
            _remoteTree.BusyChanged -= OnRemoteBusy;
            _remoteTree = null;
        }
        _resolving = false;
        _treeBusy = false;
        UpdateBusy();
        DisposeLocalWatches();
    }

    private void DisposeLocalWatches()
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

    public void Dispose()
    {
        StopWatching();
        // It listens to the projects and the tunnels, which live longer than the panel.
        Tunnels.Dispose();
    }
}
