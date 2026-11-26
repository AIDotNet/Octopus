using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Common;

/// <summary>
/// Shared state and commands for the Scheme B capsule top bar and Scheme C top-right popover menu across both
/// <c>ConversationPage</c> and <c>TerminalPage</c>: quick access to project files, git changes, branch/worktree viewer,
/// project session switcher, and session actions.
/// </summary>
public sealed partial class SessionWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;
    private readonly List<RemoteSession> _watchedSiblings = [];
    private CancellationTokenSource? _branchesCts;
    private bool _disposed;

    public SessionWorkspaceViewModel(
        RemoteSession session,
        IRemoteWorkspace workspace,
        INavigator navigator,
        SessionActionsViewModel actions,
        ICommand backCommand)
    {
        Session = session;
        _workspace = workspace;
        _navigator = navigator;
        Actions = actions;
        BackCommand = backCommand;

        session.PropertyChanged += OnSessionChanged;
        session.Project.Sessions.CollectionChanged += OnProjectSessionsChanged;
        session.Project.Workspaces.CollectionChanged += OnProjectWorkspacesChanged;
        WatchSiblings();
        SyncFromSession();
    }

    public RemoteSession Session { get; }
    public SessionActionsViewModel Actions { get; }
    public ICommand BackCommand { get; }

    [ObservableProperty] public partial bool IsPopoverOpen { get; set; }
    [ObservableProperty] public partial bool IsBranchesSheetOpen { get; set; }
    [ObservableProperty] public partial bool IsSessionsSheetOpen { get; set; }
    [ObservableProperty] public partial bool IsLoadingBranches { get; private set; }

    // Top bar status & context (updated by Session or page-specific state such as ConversationState / Terminal size).
    [ObservableProperty] public partial bool IsWorking { get; private set; }
    [ObservableProperty] public partial bool IsWaiting { get; private set; }
    [ObservableProperty] public partial bool IsError { get; private set; }
    [ObservableProperty] public partial bool ShowSpinner { get; private set; }
    [ObservableProperty] public partial string? StatusOverride { get; private set; }

    // Page-specific popover entries (Reload for ConversationPage, Fit/Release + Font size for TerminalPage).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrimaryAction))]
    public partial string? PrimaryActionLabel { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrimaryAction))]
    public partial ICommand? PrimaryActionCommand { get; set; }
    public bool HasPrimaryAction => !string.IsNullOrEmpty(PrimaryActionLabel) && PrimaryActionCommand is not null;

    [ObservableProperty] public partial bool ShowFontSizeControls { get; set; }
    [ObservableProperty] public partial string FontSizeLabel { get; set; } = "";
    [ObservableProperty] public partial ICommand? SmallerTextCommand { get; set; }
    [ObservableProperty] public partial ICommand? LargerTextCommand { get; set; }
    [ObservableProperty] public partial bool CanSmallerText { get; set; }
    [ObservableProperty] public partial bool CanLargerText { get; set; }

    public string Title => Session.Title;
    public bool IsGui => Session.IsGui;
    public string ProjectName => Session.Project.Name;
    public string WorkspaceHeaderText => $"{Text(MobileLangResourceKind.SessionMenuWorkspaceSection)} · {Session.Project.Name}";

    public string? ActiveBranchName =>
        !string.IsNullOrWhiteSpace(Session.WorkspaceBranch)
            ? Session.WorkspaceBranch
            : Session.Project.DefaultBranch;

    public string Subtitle
    {
        get
        {
            var parts = new List<string>(3) { Session.Project.Name };
            if (ActiveBranchName is { Length: > 0 } branch) parts.Add(branch);
            else parts.Add(Session.Project.Host.Name);
            if (!string.IsNullOrWhiteSpace(StatusOverride)) parts.Add(StatusOverride);
            return string.Join(" · ", parts);
        }
    }

    public int OtherAttentionCount => Session.Project.Sessions.Count(s =>
        !ReferenceEquals(s, Session) && s.Activity is SessionActivity.Waiting or SessionActivity.Error);
    public bool HasOtherAttention => OtherAttentionCount > 0;
    public string OtherAttentionText => OtherAttentionCount > 9 ? "9+" : OtherAttentionCount.ToString();

    public bool CanBrowseFiles => _workspace.SupportsFiles(Session.Project.Host);
    public bool CanViewChanges => _workspace.SupportsGit(Session.Project.Host);
    public bool CanRestart => Actions.CanRestart;

    public string BranchesBadgeText
    {
        get
        {
            var count = Session.Project.Workspaces.Count + (Session.Project.IsGitRepository ? 1 : 0);
            if (count > 1) return MobileLangResources.Format(MobileLangResourceKind.SessionBranchesCount, count);
            return ActiveBranchName ?? "";
        }
    }

    public string SessionsBadgeText => Session.Project.Sessions.Count.ToString();
    public bool IsGitRepository => Session.Project.IsGitRepository;
    public bool ShowNotGitRepo => !Session.Project.IsGitRepository && WorkspaceBranches.Count <= 1;
    public bool HasGitBranches => GitBranches.Count > 0;

    public ObservableCollection<SessionBranchItemViewModel> WorkspaceBranches { get; } = [];
    public ObservableCollection<SessionGitBranchItemViewModel> GitBranches { get; } = [];
    public ObservableCollection<SessionSwitchItemViewModel> ProjectSessions { get; } = [];

    /// <summary>Updates live status indicator and optional compact subtitle suffix from the hosting session page.</summary>
    public void UpdateLiveStatus(bool isWorking, bool isWaiting, bool isError, bool showSpinner, string? statusOverride = null)
    {
        IsWorking = isWorking;
        IsWaiting = isWaiting;
        IsError = isError;
        ShowSpinner = showSpinner;
        StatusOverride = statusOverride;
        OnPropertyChanged(nameof(Subtitle));
    }

    /// <summary>Returns the currently visible popover menu labels in top-to-bottom order (for UI verification & tests).</summary>
    public IReadOnlyList<string> VisibleMenuLabels()
    {
        var labels = new List<string>();
        if (CanBrowseFiles) labels.Add(Text(MobileLangResourceKind.SessionMenuFiles));
        if (CanViewChanges) labels.Add(Text(MobileLangResourceKind.SessionMenuChanges));
        labels.Add(Text(MobileLangResourceKind.SessionMenuBranches));
        labels.Add(Text(MobileLangResourceKind.SessionMenuSessions));
        if (HasPrimaryAction) labels.Add(PrimaryActionLabel!);
        labels.Add(Text(MobileLangResourceKind.SessionRename));
        if (CanRestart) labels.Add(Text(MobileLangResourceKind.SessionRestart));
        labels.Add(Text(MobileLangResourceKind.SessionDuplicate));
        labels.Add(Text(MobileLangResourceKind.SessionDetails));
        labels.Add(Text(MobileLangResourceKind.SessionClose));
        return labels;
    }

    [RelayCommand]
    private void ToggleMenu() => IsPopoverOpen = !IsPopoverOpen;

    [RelayCommand]
    private void OpenMenu() => IsPopoverOpen = true;

    [RelayCommand]
    private void ClosePopover() => IsPopoverOpen = false;

    [RelayCommand]
    private void BrowseFiles()
    {
        IsPopoverOpen = false;
        _navigator.ShowFiles(Session.Project, RemoteFolders.WorkspacePathOf(Session));
    }

    [RelayCommand]
    private void ViewChanges()
    {
        IsPopoverOpen = false;
        _navigator.ShowChanges(Session.Project, RemoteFolders.WorkspacePathOf(Session));
    }

    [RelayCommand]
    private void OpenBranches()
    {
        IsPopoverOpen = false;
        RebuildWorkspaceBranches();
        IsBranchesSheetOpen = true;
        if (Session.Project.IsGitRepository) _ = LoadGitBranchesAsync();
    }

    [RelayCommand]
    private void OpenSessions()
    {
        IsPopoverOpen = false;
        RebuildProjectSessions();
        IsSessionsSheetOpen = true;
    }

    [RelayCommand]
    private void RunPrimaryAction()
    {
        IsPopoverOpen = false;
        if (PrimaryActionCommand?.CanExecute(null) == true) PrimaryActionCommand.Execute(null);
    }

    [RelayCommand]
    private void Rename()
    {
        IsPopoverOpen = false;
        Actions.BeginRenameCommand.Execute(null);
    }

    [RelayCommand]
    private void Restart()
    {
        IsPopoverOpen = false;
        if (CanRestart) Actions.RestartCommand.Execute(null);
    }

    [RelayCommand]
    private void Duplicate()
    {
        IsPopoverOpen = false;
        Actions.DuplicateCommand.Execute(null);
    }

    [RelayCommand]
    private void ShowDetails()
    {
        IsPopoverOpen = false;
        Actions.ShowDetailsCommand.Execute(null);
    }

    [RelayCommand]
    private void CloseSession()
    {
        IsPopoverOpen = false;
        Actions.BeginCloseCommand.Execute(null);
    }

    partial void OnIsBranchesSheetOpenChanged(bool value)
    {
        if (!value) CancelBranchLoad();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelBranchLoad();
        Session.PropertyChanged -= OnSessionChanged;
        Session.Project.Sessions.CollectionChanged -= OnProjectSessionsChanged;
        Session.Project.Workspaces.CollectionChanged -= OnProjectWorkspacesChanged;
        UnwatchSiblings();
        ClearProjectSessions();
    }

    private void SyncFromSession()
    {
        IsWorking = Session.Activity == SessionActivity.Working;
        IsWaiting = Session.Activity == SessionActivity.Waiting;
        IsError = Session.Activity == SessionActivity.Error;
        ShowSpinner = IsWorking;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ActiveBranchName));
        OnPropertyChanged(nameof(BranchesBadgeText));
    }

    private void RebuildWorkspaceBranches()
    {
        WorkspaceBranches.Clear();
        var project = Session.Project;
        var defaultSessions = project.Sessions.Where(s => string.IsNullOrEmpty(s.WorkspaceId)).ToList();
        var defaultTitle = !string.IsNullOrWhiteSpace(project.DefaultBranch)
            ? project.DefaultBranch!
            : Text(MobileLangResourceKind.WorkspaceDefaultTitle);
        var defaultCurrent = string.IsNullOrEmpty(Session.WorkspaceId);
        WorkspaceBranches.Add(new SessionBranchItemViewModel(
            title: defaultTitle,
            subtitle: project.Path,
            isDefault: true,
            isCurrent: defaultCurrent,
            taskKey: null,
            sessionCountText: MobileLangResources.Format(MobileLangResourceKind.ProjectSessionCount, defaultSessions.Count),
            canBrowseFiles: CanBrowseFiles,
            canViewChanges: CanViewChanges && project.IsGitRepository,
            openFiles: () => OpenWorkspaceFiles(project.Path),
            openChanges: () => OpenWorkspaceChanges(project.Path)));

        foreach (var ws in project.Workspaces)
        {
            var wsSessions = project.Sessions.Count(s => s.WorkspaceId == ws.Id);
            var isCurrent = Session.WorkspaceId == ws.Id;
            var subtitle = ws.HasTask && !string.IsNullOrWhiteSpace(ws.TaskTitle)
                ? ws.TaskTitle!
                : ws.Path;
            WorkspaceBranches.Add(new SessionBranchItemViewModel(
                title: ws.DisplayName,
                subtitle: subtitle,
                isDefault: false,
                isCurrent: isCurrent,
                taskKey: ws.TaskKey,
                sessionCountText: MobileLangResources.Format(MobileLangResourceKind.ProjectSessionCount, wsSessions),
                canBrowseFiles: CanBrowseFiles,
                canViewChanges: CanViewChanges,
                openFiles: () => OpenWorkspaceFiles(ws.Path),
                openChanges: () => OpenWorkspaceChanges(ws.Path)));
        }

        OnPropertyChanged(nameof(ShowNotGitRepo));
        OnPropertyChanged(nameof(BranchesBadgeText));
    }

    private async Task LoadGitBranchesAsync()
    {
        CancelBranchLoad();
        var cts = _branchesCts = new CancellationTokenSource();
        IsLoadingBranches = true;
        try
        {
            var options = await _workspace.GetWorkspaceOptionsAsync(Session.Project, cts.Token);
            if (cts.IsCancellationRequested || _disposed) return;
            GitBranches.Clear();
            foreach (var branch in options.Branches)
            {
                GitBranches.Add(new SessionGitBranchItemViewModel(branch));
            }
            OnPropertyChanged(nameof(HasGitBranches));
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            if (ReferenceEquals(_branchesCts, cts)) IsLoadingBranches = false;
        }
    }

    private void CancelBranchLoad()
    {
        if (_branchesCts is not { } cts) return;
        _branchesCts = null;
        cts.Cancel();
        cts.Dispose();
        IsLoadingBranches = false;
    }

    private void OpenWorkspaceFiles(string folder)
    {
        IsBranchesSheetOpen = false;
        _navigator.ShowFiles(Session.Project, folder);
    }

    private void OpenWorkspaceChanges(string workspacePath)
    {
        IsBranchesSheetOpen = false;
        _navigator.ShowChanges(Session.Project, workspacePath);
    }

    private void RebuildProjectSessions()
    {
        ClearProjectSessions();
        foreach (var item in Session.Project.Sessions)
        {
            ProjectSessions.Add(new SessionSwitchItemViewModel(item, ReferenceEquals(item, Session), SelectSession));
        }
        OnPropertyChanged(nameof(SessionsBadgeText));
    }

    private void ClearProjectSessions()
    {
        foreach (var row in ProjectSessions) row.Dispose();
        ProjectSessions.Clear();
    }

    private void SelectSession(RemoteSession target)
    {
        IsSessionsSheetOpen = false;
        if (ReferenceEquals(target, Session)) return;
        _navigator.ReplaceSession(target);
    }

    private void WatchSiblings()
    {
        UnwatchSiblings();
        foreach (var sibling in Session.Project.Sessions)
        {
            if (ReferenceEquals(sibling, Session)) continue;
            sibling.PropertyChanged += OnSiblingChanged;
            _watchedSiblings.Add(sibling);
        }
    }

    private void UnwatchSiblings()
    {
        foreach (var sibling in _watchedSiblings) sibling.PropertyChanged -= OnSiblingChanged;
        _watchedSiblings.Clear();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(RemoteSession.Title):
                OnPropertyChanged(nameof(Title));
                break;
            case nameof(RemoteSession.WorkspaceBranch):
            case nameof(RemoteSession.WorkspaceId):
                OnPropertyChanged(nameof(ActiveBranchName));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(BranchesBadgeText));
                if (IsBranchesSheetOpen) RebuildWorkspaceBranches();
                break;
            case nameof(RemoteSession.Activity):
                SyncFromSession();
                break;
        }
    }

    private void OnSiblingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemoteSession.Activity)) NotifyAttentionChanged();
    }

    private void OnProjectSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        WatchSiblings();
        NotifyAttentionChanged();
        OnPropertyChanged(nameof(SessionsBadgeText));
        if (IsSessionsSheetOpen) RebuildProjectSessions();
        if (IsBranchesSheetOpen) RebuildWorkspaceBranches();
    }

    private void OnProjectWorkspacesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(BranchesBadgeText));
        if (IsBranchesSheetOpen) RebuildWorkspaceBranches();
    }

    private void NotifyAttentionChanged()
    {
        OnPropertyChanged(nameof(OtherAttentionCount));
        OnPropertyChanged(nameof(HasOtherAttention));
        OnPropertyChanged(nameof(OtherAttentionText));
    }

    private static string Text(MobileLangResourceKind kind) => MobileLangResources.Get(kind);
}

public sealed partial class SessionBranchItemViewModel : ObservableObject
{
    private readonly Action _openFiles;
    private readonly Action _openChanges;

    public SessionBranchItemViewModel(
        string title,
        string subtitle,
        bool isDefault,
        bool isCurrent,
        string? taskKey,
        string sessionCountText,
        bool canBrowseFiles,
        bool canViewChanges,
        Action openFiles,
        Action openChanges)
    {
        Title = title;
        Subtitle = subtitle;
        IsDefault = isDefault;
        IsCurrent = isCurrent;
        TaskKey = taskKey;
        SessionCountText = sessionCountText;
        CanBrowseFiles = canBrowseFiles;
        CanViewChanges = canViewChanges;
        _openFiles = openFiles;
        _openChanges = openChanges;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public bool IsDefault { get; }
    public bool IsWorktree => !IsDefault;
    public bool IsCurrent { get; }
    public string? TaskKey { get; }
    public bool HasTaskKey => !string.IsNullOrWhiteSpace(TaskKey);
    public string SessionCountText { get; }
    public bool CanBrowseFiles { get; }
    public bool CanViewChanges { get; }

    [RelayCommand]
    private void BrowseFiles() => _openFiles();

    [RelayCommand]
    private void ViewChanges() => _openChanges();
}

public sealed class SessionGitBranchItemViewModel(RemoteBranchOption branch)
{
    public string Name { get; } = branch.Name;
    public bool IsRemote { get; } = branch.IsRemote;
    public bool IsCurrent { get; } = branch.IsCurrent;
    public string? OccupiedBy { get; } = branch.OccupiedBy;
    public bool HasOccupiedBy => !string.IsNullOrWhiteSpace(OccupiedBy);
    public string KindLabel => MobileLangResources.Get(IsRemote
        ? MobileLangResourceKind.SessionBranchesRemote
        : MobileLangResourceKind.SessionBranchesLocal);
}

public sealed partial class SessionSwitchItemViewModel : ObservableObject, IDisposable
{
    private readonly Action<RemoteSession> _select;

    public SessionSwitchItemViewModel(RemoteSession session, bool isCurrent, Action<RemoteSession> select)
    {
        Session = session;
        IsCurrent = isCurrent;
        _select = select;
        var agent = session.AgentId is null ? null : session.Project.Host.Agents.FirstOrDefault(a => a.Id == session.AgentId);
        IsAgent = session.Kind == SessionKind.Agent;
        Initial = agent?.Initial ?? (IsAgent ? "A" : "");
        Tint = agent is not null && Color.TryParse(agent.Color, out var color) ? new SolidColorBrush(color) : null;
        session.PropertyChanged += OnSessionChanged;
    }

    public RemoteSession Session { get; }
    public bool IsCurrent { get; }
    public bool IsAgent { get; }
    public bool IsTerminal => !IsAgent;
    public string Initial { get; }
    public IBrush? Tint { get; }
    public string Title => Session.Title;
    public bool IsGui => Session.IsGui;
    public string? BranchText => Session.WorkspaceBranch ?? Session.Project.DefaultBranch;
    public bool HasBranch => !string.IsNullOrWhiteSpace(BranchText);
    public string? Preview => Session.Preview ?? Session.Detail;
    public bool HasPreview => !string.IsNullOrWhiteSpace(Preview);
    public bool IsWaiting => Session.Activity == SessionActivity.Waiting;
    public bool IsWorking => Session.Activity == SessionActivity.Working;
    public bool IsError => Session.Activity == SessionActivity.Error;
    public bool HasStatus => Session.Activity != SessionActivity.Idle;
    public string StatusText => SessionRowViewModel.StatusOf(Session.Activity);

    [RelayCommand]
    private void Select() => _select(Session);

    public void Dispose() => Session.PropertyChanged -= OnSessionChanged;

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(string.Empty);
}
