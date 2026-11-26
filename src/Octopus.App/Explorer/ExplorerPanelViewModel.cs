using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Octopus.App.Common;
using Octopus.App.Explorer.Files;
using Octopus.App.Explorer.Git;
using Octopus.App.Explorer.Search;
using Octopus.App.Explorer.Sessions;
using Octopus.App.Explorer.Subagent;
using Octopus.App.Explorer.Tunnels;
using Octopus.App.Files;
using Octopus.App.Localization;
using Octopus.App.Plugins;
using Octopus.App.Services;
using Octopus.App.Sidebar;
using Octopus.Core.Projects;
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
    private IRemoteFileTree? _remoteTree;
    private readonly Octopus.App.Remote.RemoteProjectAccess? _access;
    private readonly PluginPanels? _pluginPanels;
    private readonly IProjectService? _projectService;
    private string _activeDeviceKey = DeviceKeys.Local;
    private IDisposable? _watch;
    private IDisposable? _changeWatch;
    private IDisposable? _gitWatch;
    private int _version;
    private bool _isActive = true;
    /// <summary>The project the shell asked for while the panel was closed: it is read when the panel opens.</summary>
    private (string? Path, string? Name, string? SshHost, string? RemoteHost, string? RootPath, string? WorkspaceBranch)? _pendingProject;
    /// <summary>The local folder's watches were stopped while the panel was closed: on opening, they restart and the panel catches up.</summary>
    private bool _paused;
    private GitDirectories? _gitDirectories;
    /// <summary>The git lookup of the current folder finished (whether or not it is in a repository).</summary>
    private bool _gitLookedUp;
    private readonly Lock _pendingDirsLock = new();
    private readonly HashSet<string> _pendingDirs = new(StringComparer.Ordinal);
    private bool _pendingDirsScheduled;
    private ExplorerTab _lastNonSubagentTab = ExplorerTab.Files;
    /// <summary>The project the subagent tab belongs to (see <see cref="SubagentProjectKey"/>); "" before any project.</summary>
    private string _subagentProjectKey = "";
    /// <summary>Per project: the subagent the tab showed when the user left it, and whether the tab was the one on show.</summary>
    private readonly Dictionary<string, (OpenSubagentPanelRequested Target, bool Showing)> _subagentByProject = new(StringComparer.Ordinal);

    public ExplorerPanelViewModel(
        FileTreeViewModel files,
        FileSearchViewModel search,
        GitPanelViewModel gitPanel,
        TunnelsPanelViewModel tunnels,
        SessionsPanelViewModel sessions,
        IFileTreeService tree,
        IGitService git,
        ISettingsStore settings,
        RemoteFiles remote,
        Octopus.App.Remote.RemoteProjectAccess? access = null,
        PluginPanels? pluginPanels = null,
        IProjectService? projectService = null,
        SubagentPanelViewModel? subagent = null)
    {
        _remote = remote;
        _access = access;
        _pluginPanels = pluginPanels;
        _projectService = projectService;
        Files = files;
        Search = search;
        Git = gitPanel;
        Tunnels = tunnels;
        Sessions = sessions;
        Subagent = subagent ?? new SubagentPanelViewModel();
        _tree = tree;
        _git = git;
        _settings = settings;
        var savedTab = settings.Current.RightPanel.Tab;
        if (PluginPanels.TryParseKey(savedTab, out var savedPanelId))
        {
            ActivePluginPanelId = savedPanelId;
            Tab = ExplorerTab.Files;
        }
        else
        {
            Tab = savedTab switch
            {
                "git" => ExplorerTab.Git,
                "tunnels" => ExplorerTab.Tunnels,
                "sessions" => ExplorerTab.Sessions,
                _ => ExplorerTab.Files,
            };
        }
        _lastNonSubagentTab = Tab;

        if (_projectService is not null)
        {
            _activeDeviceKey = DeviceKeys.Normalize(_projectService.ActiveDeviceKey);
            _projectService.Changed += OnProjectServiceChanged;
        }
        _pluginPanels?.Changed += OnPluginPanelsChanged;
        RefreshPluginPanels();

        files.OpenRequested += (_, path) => FileOpenRequested?.Invoke(this, path);
        search.OpenRequested += (_, path) => FileOpenRequested?.Invoke(this, path);
        gitPanel.OpenRequested += (_, path) => FileOpenRequested?.Invoke(this, path);
        gitPanel.DiffRequested += (_, request) => DiffRequested?.Invoke(this, request);
        gitPanel.StatusChanged += OnGitStatusChanged;
        Subagent.FileOpenRequested += (_, path) => FileOpenRequested?.Invoke(this, path);
        Subagent.DiffRequested += (_, request) => DiffRequested?.Invoke(this, request);
        Subagent.CloseRequested += (_, _) => OnSubagentClosed();
        Subagent.PropertyChanged += OnSubagentPropertyChanged;
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
    public SubagentPanelViewModel Subagent { get; }

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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkspaceBanner), nameof(WorkspaceBannerText))]
    public partial string? WorkspaceBranch { get; private set; }
    [ObservableProperty] public partial bool IsGitRepository { get; private set; }

    /// <summary>The project is on an SSH host: the panel then has a tunnels tab (and no git, no search).</summary>
    [ObservableProperty] public partial bool IsSshProject { get; private set; }

    /// <summary>The project is on a computer that runs Octopus: files and git are that computer's; sessions and tunnels have no tab.</summary>
    [ObservableProperty] public partial bool IsHostProject { get; private set; }

    /// <summary>The number of changed files, for the badge on the Git tab.</summary>
    [ObservableProperty] public partial int ChangeCount { get; private set; }

    /// <summary>The tab the person chose; it falls back to files when the folder is not in a repository.</summary>
    [ObservableProperty] public partial ExplorerTab Tab { get; private set; }

    /// <summary>The plugin panel id currently selected, or null when a built-in tab is active.</summary>
    [ObservableProperty] public partial string? ActivePluginPanelId { get; private set; }

    /// <summary>Plugin-contributed right panels eligible for the current device/project context.</summary>
    public IReadOnlyList<PluginPanelItem> VisiblePluginPanels { get; private set; } = [];

    /// <summary>Raised when the list or badges of visible plugin panels changed.</summary>
    public event EventHandler? PluginPanelsChanged;

    public bool HasProject => ProjectPath is not null;
    public bool HasSubagentTab => Subagent.HasTarget;
    public bool HasAnyTab => HasProject || HasSubagentTab || VisiblePluginPanels.Count > 0;
    public bool IsPluginPanelTab => ActivePluginPanelId is { Length: > 0 } id && VisiblePluginPanels.Any(p => p.Id == id);
    public bool IsSubagentTab => HasSubagentTab && !IsPluginPanelTab && Tab == ExplorerTab.Subagent;
    /// <summary>Lazy-mounted singleton subagent view model: null while hidden or on another tab so zero visual nodes remain resident.</summary>
    public SubagentPanelViewModel? MountedSubagent => _isActive && IsSubagentTab ? Subagent : null;
    public bool HasNoProject => ProjectPath is null && !IsPluginPanelTab && !IsSubagentTab;
    public bool IsFilesTab => HasProject && !IsGitTab && !IsTunnelsTab && !IsSessionsTab && !IsSubagentTab && !IsPluginPanelTab;
    public bool IsGitTab => HasProject && IsGitRepository && !IsPluginPanelTab && Tab == ExplorerTab.Git;
    public bool IsTunnelsTab => HasProject && IsSshProject && !IsPluginPanelTab && Tab == ExplorerTab.Tunnels;
    /// <summary>Sessions are read from this computer: a project on an SSH host has none here.</summary>
    public bool IsSessionsTab => HasProject && !IsSshProject && !IsHostProject && !IsPluginPanelTab && Tab == ExplorerTab.Sessions;
    public bool HasChanges => ChangeCount > 0;
    /// <summary>Shown above Files and Git tabs when the right panel is following a non-default git worktree.</summary>
    public bool HasWorkspaceBanner => !string.IsNullOrEmpty(WorkspaceBranch) && (IsFilesTab || IsGitTab);
    public string WorkspaceBannerText => string.IsNullOrEmpty(WorkspaceBranch)
        ? ""
        : AppLangResources.Format(AppLangResourceKind.WorkspaceScopeFollowing, WorkspaceBranch);

    /// <summary>The numbers next to the tab icons: changed files, running tunnels, active subagents.</summary>
    public string ChangeCountText => ChangeCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
    public string RunningTunnelsText => Tunnels.RunningCount.ToString(System.Globalization.CultureInfo.CurrentCulture);
    public string SubagentCountText => Subagent.SiblingCountText;
    public bool HasSubagentCount => Subagent.HasCountBadge;
    public string SubagentTabTooltip => Subagent.TabTooltip;

    /// <summary>The key of the tab showing ("files", "git", "tunnels", "sessions", "subagent" or "panel:&lt;id&gt;"), for the tab switch; setting it chooses that tab.</summary>
    public string TabKey
    {
        get => IsPluginPanelTab ? PluginPanels.KeyFor(ActivePluginPanelId!)
            : IsSubagentTab ? "subagent"
            : IsGitTab ? "git"
            : IsTunnelsTab ? "tunnels"
            : IsSessionsTab ? "sessions"
            : !HasProject && VisiblePluginPanels.Count > 0 ? VisiblePluginPanels[0].TabKey
            : "files";
        set
        {
            if (PluginPanels.TryParseKey(value, out var panelId)) ShowPluginPanel(panelId);
            else if (value == "subagent") ShowSubagentTab();
            else if (value == "git") ShowGit();
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
        RefreshPluginPanels();
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(HasAnyTab));
        OnPropertyChanged(nameof(HasNoProject));
        NotifyTabs();
    }

    partial void OnIsGitRepositoryChanged(bool value)
    {
        RefreshPluginPanels();
        NotifyTabs();
    }

    partial void OnIsSshProjectChanged(bool value)
    {
        RefreshPluginPanels();
        NotifyTabs();
    }

    partial void OnIsHostProjectChanged(bool value)
    {
        RefreshPluginPanels();
        NotifyTabs();
    }

    partial void OnActivePluginPanelIdChanged(string? value) => NotifyTabs();

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
        OnPropertyChanged(nameof(HasSubagentTab));
        OnPropertyChanged(nameof(HasAnyTab));
        OnPropertyChanged(nameof(HasNoProject));
        OnPropertyChanged(nameof(IsPluginPanelTab));
        OnPropertyChanged(nameof(IsSubagentTab));
        OnPropertyChanged(nameof(IsFilesTab));
        OnPropertyChanged(nameof(IsGitTab));
        OnPropertyChanged(nameof(IsTunnelsTab));
        OnPropertyChanged(nameof(IsSessionsTab));
        OnPropertyChanged(nameof(HasWorkspaceBanner));
        OnPropertyChanged(nameof(TabKey));
        Sessions.IsActive = _isActive && IsSessionsTab;
        Subagent.IsActive = _isActive && IsSubagentTab;
        OnPropertyChanged(nameof(MountedSubagent));
    }

    private void OnSubagentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SubagentPanelViewModel.HasTarget))
        {
            NotifyTabs();
        }
        else if (e.PropertyName is nameof(SubagentPanelViewModel.SiblingCountText) or nameof(SubagentPanelViewModel.HasCountBadge))
        {
            OnPropertyChanged(nameof(SubagentCountText));
            OnPropertyChanged(nameof(HasSubagentCount));
        }
        else if (e.PropertyName is nameof(SubagentPanelViewModel.TabTooltip) or nameof(SubagentPanelViewModel.DisplayTitle))
        {
            OnPropertyChanged(nameof(SubagentTabTooltip));
        }
    }

    private void OnSubagentClosed()
    {
        if (Tab == ExplorerTab.Subagent)
        {
            Tab = _lastNonSubagentTab;
        }
        NotifyTabs();
        WeakReferenceMessenger.Default.Send(new SubagentPanelClosed());
    }

    /// <summary>Opens the requested subagent in the singleton right-panel tab (Scheme A).</summary>
    public void ShowSubagent(OpenSubagentPanelRequested request)
    {
        var effectiveDir = request.WorkingDirectory ?? ProjectPath;
        var enriched = string.Equals(effectiveDir, request.WorkingDirectory, StringComparison.Ordinal)
            ? request
            : request with { WorkingDirectory = effectiveDir };
        Subagent.Open(enriched);
        if (!Subagent.HasTarget) return;
        ActivePluginPanelId = null;
        Tab = ExplorerTab.Subagent;
        NotifyTabs();
    }

    /// <summary>Switches to the dynamic Subagent tab if a target is currently active.</summary>
    [RelayCommand]
    public void ShowSubagentTab()
    {
        if (!HasSubagentTab) return;
        if (Tab == ExplorerTab.Subagent && ActivePluginPanelId is null) return;
        ActivePluginPanelId = null;
        Tab = ExplorerTab.Subagent;
        NotifyTabs();
    }

    /// <summary>
    /// Which project a subagent belongs to: the device plus the project's main root, so moving between a project's
    /// worktrees keeps the subagent while moving to another project does not.
    /// </summary>
    private static string SubagentProjectKey(string? path, string? sshHost, string? remoteHost, string? rootPath)
    {
        var folder = rootPath ?? path;
        if (folder is null) return "";
        var device = remoteHost is { Length: > 0 } ? DeviceKeys.Remote(remoteHost)
            : sshHost is { Length: > 0 } ? DeviceKeys.Ssh(sshHost)
            : "local";
        return device + "|" + folder.TrimEnd('/', '\\');
    }

    /// <summary>
    /// The subagent tab follows the project: the one shown in the project being left is put aside, and the one last shown
    /// in the project being entered (if its agent still exists) comes back, on show again if it was on show then.
    /// </summary>
    private void FollowProjectWithSubagent(string key, bool checkItemExists)
    {
        if (string.Equals(key, _subagentProjectKey, StringComparison.Ordinal)) return;

        var showing = Tab == ExplorerTab.Subagent && ActivePluginPanelId is null;
        if (Subagent.CurrentTarget is { } current)
        {
            _subagentByProject[_subagentProjectKey] = (current, showing);
        }
        else
        {
            _subagentByProject.Remove(_subagentProjectKey);
        }
        _subagentProjectKey = key;
        Subagent.Clear();

        if (_subagentByProject.Remove(key, out var saved)
            && (!checkItemExists || _projectService is null || _projectService.FindItem(saved.Target.ParentItemId) is not null))
        {
            Subagent.Open(saved.Target);
        }

        if (Subagent.HasTarget && saved.Showing && ActivePluginPanelId is null)
        {
            Tab = ExplorerTab.Subagent;
        }
        else if (Tab == ExplorerTab.Subagent && !Subagent.HasTarget)
        {
            Tab = _lastNonSubagentTab;
        }
        NotifyTabs();
    }

    private void OnProjectServiceChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (e.Kind is ProjectChangeKind.Device or ProjectChangeKind.CurrentProject)
        {
            _activeDeviceKey = DeviceKeys.Normalize(_projectService?.ActiveDeviceKey);
            _ui.Post(RefreshPluginPanels);
        }
    }

    private void OnPluginPanelsChanged(object? sender, EventArgs e) => _ui.Post(RefreshPluginPanels);

    private void RefreshPluginPanels()
    {
        if (_pluginPanels is null) return;
        var next = _pluginPanels.Items
            .Where(item => PluginPanels.MatchesContext(item.Contribution, HasProject, IsGitRepository, _activeDeviceKey))
            .ToList();
        VisiblePluginPanels = next;
        // When there is no project open but a device/global plugin panel exists, select the first one automatically.
        if (!HasProject && ActivePluginPanelId is null && next.Count > 0)
        {
            ActivePluginPanelId = next[0].Id;
        }
        OnPropertyChanged(nameof(VisiblePluginPanels));
        NotifyTabs();
        PluginPanelsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowPluginPanel(string panelId)
    {
        if (!VisiblePluginPanels.Any(p => p.Id == panelId)) return;
        if (ActivePluginPanelId == panelId) return;
        ActivePluginPanelId = panelId;
        _settings.Update(s => s.RightPanel.Tab = PluginPanels.KeyFor(panelId));
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
        if (IsSshProject || IsHostProject) return;
        SelectTab(ExplorerTab.Sessions);
    }

    private void SelectTab(ExplorerTab tab)
    {
        if (Tab == tab && ActivePluginPanelId is null) return;
        ActivePluginPanelId = null;
        if (tab != ExplorerTab.Subagent)
        {
            _lastNonSubagentTab = tab;
        }
        Tab = tab;
        if (tab != ExplorerTab.Subagent)
        {
            _settings.Update(s => s.RightPanel.Tab = tab switch
            {
                ExplorerTab.Git => "git",
                ExplorerTab.Tunnels => "tunnels",
                ExplorerTab.Sessions => "sessions",
                _ => "files",
            });
        }
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
            Subagent.IsActive = value && IsSubagentTab;
            OnPropertyChanged(nameof(MountedSubagent));
            if (value) _ = ResumeAsync();
            else Pause();
        }
    }

    private void Pause()
    {
        // A tree on another machine only reads when asked (or told by its host), so it is left as it is; a local folder's watches stop.
        if (ProjectPath is null || IsSshProject || IsHostProject || _pendingProject is not null) return;
        _version++; // a git lookup still running for this folder must not start watches now
        DisposeLocalWatches();
        _paused = true;
    }

    private async Task ResumeAsync()
    {
        if (_pendingProject is { } pending)
        {
            await SetProjectAsync(pending.Path, pending.Name, pending.SshHost, pending.RemoteHost, pending.RootPath, pending.WorkspaceBranch);
            return;
        }
        if (!_paused || ProjectPath is not { } folder) return;
        _paused = false;
        var version = ++_version;
        if (!_gitLookedUp)
        {
            // The git lookup had not finished when the panel closed: the folder is read again from the start.
            await SetProjectAsync(folder, ProjectName, workspaceBranch: WorkspaceBranch);
            return;
        }
        WatchLocal(folder);
        await Files.ReloadAsync();
        if (version == _version) await Git.RefreshAsync(includeLog: IsGitTab);
    }

    /// <summary>
    /// Points the panel at a project folder (null: none): loads the tree, finds out about git and starts watching. A folder
    /// on an SSH host (<paramref name="sshHost"/>) is read through the host: there is no git and no search for it here.
    /// While the panel is closed (<see cref="IsActive"/>) this only remembers the folder.
    /// </summary>
    /// <param name="remoteHost">The device id of the Octopus host the folder is on: read through that host, with its git.</param>
    /// <param name="rootPath">The parent project's main root folder when <paramref name="path"/> is a worktree; used by Sessions.</param>
    /// <param name="workspaceBranch">Branch name of the active worktree when viewing one; null for the default project directory.</param>
    public async Task SetProjectAsync(string? path, string? name, string? sshHost = null, string? remoteHost = null, string? rootPath = null, string? workspaceBranch = null)
    {
        var version = ++_version;
        StopWatching();
        _paused = false;
        // Done even while the panel is closed: on opening, it should not still show the last project's subagent.
        FollowProjectWithSubagent(SubagentProjectKey(path, sshHost, remoteHost, rootPath), checkItemExists: remoteHost is null);
        if (!_isActive)
        {
            _pendingProject = (path, name, sshHost, remoteHost, rootPath, workspaceBranch);
            return;
        }
        _pendingProject = null;
        _gitDirectories = null;
        _gitLookedUp = false;
        WorkspaceBranch = workspaceBranch;
        // Without the service that reaches hosts there is nothing to read the folder through: no project is shown.
        if (remoteHost is not null && _access is null) path = null;
        _activeDeviceKey = remoteHost is { Length: > 0 } ? DeviceKeys.Remote(remoteHost)
            : sshHost is { Length: > 0 } ? DeviceKeys.Ssh(sshHost)
            : DeviceKeys.Normalize(_projectService?.ActiveDeviceKey);
        IsSshProject = path is not null && sshHost is not null;
        // Sessions always aggregate the whole project (default + all worktrees), so it receives the main project root.
        var sessionsRoot = rootPath ?? path;
        Sessions.SetProject(sshHost is null && remoteHost is null && sessionsRoot is not null && Directory.Exists(sessionsRoot) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionsRoot)) : null);
        IsHostProject = path is not null && remoteHost is not null;
        if (path is not null && remoteHost is not null && _access is not null)
        {
            await SetRemoteProjectAsync(path, name, _access.TreeOfHost(remoteHost), _access.GitOfHost(remoteHost), version);
            return;
        }
        if (path is not null && sshHost is not null)
        {
            await SetRemoteProjectAsync(path, name, _remote.TreeFor(sshHost), null, version);
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
        _changeWatch = _tree.WatchChanges(folder, OnFolderChange, GitRefreshDelay);
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
        else _ = Files.ReloadAsync();
    }

    private void OnDirectoryChanged(string directory)
    {
        // Called on a background thread by the watcher: coalesce bursts into one UI dispatch.
        bool schedule;
        lock (_pendingDirsLock)
        {
            _pendingDirs.Add(directory);
            schedule = !_pendingDirsScheduled;
            _pendingDirsScheduled = true;
        }
        if (!schedule) return;
        _ui.Post(() =>
        {
            string[] dirs;
            lock (_pendingDirsLock)
            {
                dirs = [.. _pendingDirs];
                _pendingDirs.Clear();
                _pendingDirsScheduled = false;
            }
            foreach (var dir in dirs) _ = Files.RefreshDirectoryAsync(dir);
        });
    }

    private void OnFolderChange(bool gitMetadataChanged)
    {
        // Plain file writes and `git add` only affect `git status`; commits/branches/stashes change only when refs/HEAD move.
        _ui.Post(() =>
        {
            if (IsGitRepository) _ = Git.RefreshAsync(includeLog: gitMetadataChanged && IsGitTab);
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
    /// <param name="git">The git service of the machine the folder is on; null where there is none to ask (an SSH host).</param>
    private async Task SetRemoteProjectAsync(string path, string? name, IRemoteFileTree tree, IGitService? git, int version)
    {
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
        catch (Exception e) when (e is SshCommandException or IOException)
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
        if (git is null) return;

        // A host that runs Octopus has git too: asked there, and refreshed when the host says the folder changed.
        var repository = await git.FindRepositoryRootAsync(root);
        if (version != _version) return;
        _gitLookedUp = true;
        if (repository is null) return;
        Git.SetRepository(repository, git);
        IsGitRepository = true;
        _changeWatch = tree.WatchChanges(root, OnAnyChange, GitRefreshDelay);
        await Git.RefreshAsync(includeLog: IsGitTab);
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
        _version++; // prevent an in-flight SetProjectAsync from starting new watches after Dispose
        _projectService?.Changed -= OnProjectServiceChanged;
        _pluginPanels?.Changed -= OnPluginPanelsChanged;
        StopWatching();
        Subagent.PropertyChanged -= OnSubagentPropertyChanged;
        Subagent.Dispose();
        // It listens to the projects and the tunnels, which live longer than the panel.
        Tunnels.Dispose();
    }
}
