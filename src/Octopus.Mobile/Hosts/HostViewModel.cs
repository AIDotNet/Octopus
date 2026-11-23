using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Core.Projects;
using Octopus.Mobile.Common;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Hosts;

/// <summary>One computer: its projects as groups (split by branch workspaces when present), and the sheet that starts a new session.</summary>
public sealed partial class HostViewModel : ObservableObject, IDisposable
{
    private readonly INavigator _navigator;

    public HostViewModel(RemoteHost host, IRemoteWorkspace workspace, INavigator navigator)
    {
        Host = host;
        _navigator = navigator;
        NewSession = new NewSessionViewModel(host, workspace, navigator.ShowSession);
        host.Projects.CollectionChanged += OnProjectsChanged;
        Rebuild();
    }

    public RemoteHost Host { get; }
    public string Title => Host.Name;
    public ObservableCollection<ProjectSectionViewModel> Sections { get; } = [];
    public NewSessionViewModel NewSession { get; }
    [ObservableProperty] public partial bool IsEmpty { get; private set; }

    [RelayCommand]
    private void Back() => _navigator.Back();

    [RelayCommand]
    private void OpenNewSession() => NewSession.Open();

    public void Dispose()
    {
        Host.Projects.CollectionChanged -= OnProjectsChanged;
        NewSession.Dispose();
        foreach (var section in Sections) section.Dispose();
    }

    private void OnProjectsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        foreach (var section in Sections) section.Dispose();
        Sections.Clear();
        foreach (var project in Host.Projects)
        {
            Sections.Add(new ProjectSectionViewModel(project, _navigator.ShowSession,
                (p, workspaceId, startNewWorkspace) => NewSession.Open(p, workspaceId, startNewWorkspace)));
        }
        IsEmpty = Sections.Count == 0;
    }
}

/// <summary>
/// A project's section: when the project has no branch worktrees it renders as a single flat list; when worktrees exist
/// it groups sessions under Default and each branch workspace with quick "+" actions.
/// </summary>
public sealed partial class ProjectSectionViewModel : ObservableObject, IDisposable
{
    private readonly Action<RemoteSession> _open;
    private readonly Action<RemoteProject, string?, bool> _openNew;

    public ProjectSectionViewModel(RemoteProject project, Action<RemoteSession> open, Action<RemoteProject, string?, bool> openNew)
    {
        Project = project;
        _open = open;
        _openNew = openNew;
        project.Sessions.CollectionChanged += OnChildrenChanged;
        project.Workspaces.CollectionChanged += OnChildrenChanged;
        Rebuild();
    }

    public RemoteProject Project { get; }
    public string Header => Project.Name + " · " + Project.Path;
    public bool HasWorkspaces => Project.Workspaces.Count > 0;
    /// <summary>All sessions of this project in order.</summary>
    public ObservableCollection<SessionRowViewModel> Sessions { get; } = [];
    /// <summary>Bound to the flat ListGroup only when <see cref="HasWorkspaces"/> is false (prevents duplicate visual rows).</summary>
    public ObservableCollection<SessionRowViewModel> FlatSessions { get; } = [];
    /// <summary>Bound to the grouped workspace view only when <see cref="HasWorkspaces"/> is true.</summary>
    public ObservableCollection<MobileWorkspaceGroupViewModel> WorkspaceGroups { get; } = [];

    [RelayCommand]
    private void NewBranch() => _openNew(Project, null, true);

    public void Dispose()
    {
        Project.Sessions.CollectionChanged -= OnChildrenChanged;
        Project.Workspaces.CollectionChanged -= OnChildrenChanged;
        ClearRows();
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        ClearRows();

        var allRows = new List<(RemoteSession Session, SessionRowViewModel Row)>(Project.Sessions.Count);
        foreach (var session in Project.Sessions)
        {
            var row = new SessionRowViewModel(session, _open);
            Sessions.Add(row);
            allRows.Add((session, row));
        }

        if (!HasWorkspaces)
        {
            foreach (var (_, row) in allRows) FlatSessions.Add(row);
        }
        else
        {
            var knownIds = Project.Workspaces.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
            var defaultRows = allRows.Where(x => string.IsNullOrEmpty(x.Session.WorkspaceId) || !knownIds.Contains(x.Session.WorkspaceId)).Select(x => x.Row);
            WorkspaceGroups.Add(new MobileWorkspaceGroupViewModel(Project, null, defaultRows, () => _openNew(Project, null, false)));

            foreach (var ws in Project.Workspaces)
            {
                var wsRows = allRows.Where(x => x.Session.WorkspaceId == ws.Id).Select(x => x.Row);
                WorkspaceGroups.Add(new MobileWorkspaceGroupViewModel(Project, ws, wsRows, () => _openNew(Project, ws.Id, false)));
            }
        }

        OnPropertyChanged(nameof(HasWorkspaces));
    }

    private void ClearRows()
    {
        foreach (var row in Sessions) row.Dispose();
        Sessions.Clear();
        FlatSessions.Clear();
        WorkspaceGroups.Clear();
    }
}

/// <summary>One workspace bucket inside a project section on the host page: Default folder or a git branch worktree.</summary>
public sealed partial class MobileWorkspaceGroupViewModel : ObservableObject
{
    private readonly Action _add;

    public MobileWorkspaceGroupViewModel(RemoteProject project, RemoteBranchWorkspace? workspace, IEnumerable<SessionRowViewModel> sessions, Action add)
    {
        Project = project;
        Workspace = workspace;
        _add = add;
        foreach (var row in sessions) Sessions.Add(row);
    }

    public RemoteProject Project { get; }
    public RemoteBranchWorkspace? Workspace { get; }
    public bool IsDefault => Workspace is null;
    public bool IsWorktree => Workspace is not null;
    public string Title => Workspace is not null
        ? Workspace.DisplayName
        : !string.IsNullOrWhiteSpace(Project.DefaultBranch)
            ? $"{MobileLangResources.Get(MobileLangResourceKind.WorkspaceDefault)} · {Project.DefaultBranch}"
            : MobileLangResources.Get(MobileLangResourceKind.WorkspaceDefault);
    public string? TaskKey => Workspace?.TaskKey;
    public bool HasTask => Workspace?.HasTask == true;
    public ObservableCollection<SessionRowViewModel> Sessions { get; } = [];
    public bool IsEmpty => Sessions.Count == 0;

    [RelayCommand]
    private void AddSession() => _add();
}

/// <summary>The "New" sheet: project, optional branch workspace (existing or newly created with task link), what to run, and agent options.</summary>
public sealed partial class NewSessionViewModel : ObservableObject, IDisposable
{
    private readonly RemoteHost _host;
    private readonly IRemoteWorkspace _workspace;
    private readonly Action<RemoteSession> _created;
    private CancellationTokenSource? _optionsCts;
    private RemoteWorkspaceOptions _options = RemoteWorkspaceOptions.Empty;
    private string? _optionsProjectId;
    private bool _applyingSuggestedBranch;
    private bool _branchEditedManually;

    public NewSessionViewModel(RemoteHost host, IRemoteWorkspace workspace, Action<RemoteSession> created)
    {
        _host = host;
        _workspace = workspace;
        _created = created;
    }

    [ObservableProperty] public partial bool IsOpen { get; set; }
    public ObservableCollection<ProjectChoiceViewModel> Projects { get; } = [];
    public ObservableCollection<WorkspaceChoiceViewModel> Workspaces { get; } = [];
    public bool ShowWorkspaces => Workspaces.Count > 1;
    public WorkspaceChoiceViewModel? SelectedWorkspaceChoice => Workspaces.FirstOrDefault(choice => choice.IsSelected);
    public bool IsCreatingWorkspace => SelectedWorkspaceChoice?.IsCreateNew == true;

    // Inline new branch workspace creation fields
    public ObservableCollection<string> BranchModes { get; } = [];
    [ObservableProperty] public partial int SelectedBranchMode { get; set; }
    public bool IsNewBranchMode => SelectedBranchMode == 0;
    public bool IsExistingBranchMode => SelectedBranchMode == 1;

    public ObservableCollection<RemoteBranchOption> BaseBranches { get; } = [];
    [ObservableProperty] public partial string? SelectedBaseBranch { get; set; }
    public string BaseBranchText => SelectedBaseBranch ?? Project?.DefaultBranch ?? "HEAD";
    [ObservableProperty] public partial bool IsBaseBranchMenuOpen { get; set; }
    [ObservableProperty] public partial string NewBranchName { get; set; } = "";

    public ObservableCollection<RemoteBranchOption> AvailableExistingBranches { get; } = [];
    [ObservableProperty] public partial RemoteBranchOption? SelectedExistingBranch { get; set; }
    public bool HasAvailableExistingBranches => AvailableExistingBranches.Count > 0;
    public string ExistingBranchText => SelectedExistingBranch?.Name ?? MobileLangResources.Get(MobileLangResourceKind.WorkspaceExistingBranchEmpty);
    [ObservableProperty] public partial bool IsExistingBranchMenuOpen { get; set; }

    public string WorkspacePathPreview
    {
        get
        {
            var projectName = Project?.Name ?? "project";
            var rawBranch = IsNewBranchMode ? NewBranchName : SelectedExistingBranch?.Name;
            var branch = !IsNewBranchMode && SelectedExistingBranch is { IsRemote: true } && rawBranch is not null && rawBranch.IndexOf('/') is var slash and >= 0
                ? rawBranch[(slash + 1)..]
                : rawBranch;
            var projectSeg = WorkspacePaths.SanitizeSegment(projectName, "project");
            var branchSeg = string.IsNullOrWhiteSpace(branch) ? "<branch>" : WorkspacePaths.SanitizeSegment(branch, "branch");
            var baseDir = !string.IsNullOrWhiteSpace(_options.WorkspacesDirectory)
                ? _options.WorkspacesDirectory.TrimEnd('/', '\\')
                : $"~/.octopus/workspaces/{projectSeg}";
            var sep = baseDir.Contains('\\') && !baseDir.Contains('/') ? '\\' : '/';
            return MobileLangResources.Format(MobileLangResourceKind.WorkspacePathPreview, $"{baseDir}{sep}{branchSeg}");
        }
    }

    // Optional task binding for new branch workspaces
    public ObservableCollection<string> TaskModes { get; } = [];
    [ObservableProperty] public partial int SelectedTaskMode { get; set; }
    public bool IsRemoteTaskMode => SelectedTaskMode is 1 or 2;
    public bool IsNoteTaskMode => SelectedTaskMode == 3;
    [ObservableProperty] public partial bool IsLoadingWorkspaceOptions { get; private set; }
    [ObservableProperty] public partial bool GhAvailable { get; private set; }
    public ObservableCollection<RemoteTaskOption> CurrentTaskOptions { get; } = [];
    [ObservableProperty] public partial RemoteTaskOption? SelectedTaskOption { get; set; }
    [ObservableProperty] public partial bool IsTaskMenuOpen { get; set; }
    public bool HasCurrentTaskOptions => CurrentTaskOptions.Count > 0;
    public string TaskPickerText
    {
        get
        {
            if (SelectedTaskOption is { } picked) return $"{picked.Key} · {picked.Title}";
            if (IsLoadingWorkspaceOptions) return MobileLangResources.Get(MobileLangResourceKind.WorkspaceTaskLoading);
            if (!GhAvailable) return MobileLangResources.Get(MobileLangResourceKind.WorkspaceTaskGhUnavailable);
            if (CurrentTaskOptions.Count == 0) return MobileLangResources.Get(MobileLangResourceKind.WorkspaceTaskEmpty);
            return MobileLangResources.Get(MobileLangResourceKind.WorkspaceTaskPickerPlaceholder);
        }
    }

    [ObservableProperty] public partial string TaskNote { get; set; } = "";
    [ObservableProperty] public partial string TaskUrl { get; set; } = "";
    [ObservableProperty] public partial bool InjectTaskPrompt { get; set; }

    private bool HasNewTaskBinding =>
        (IsRemoteTaskMode && SelectedTaskOption is not null)
        || (IsNoteTaskMode && (!string.IsNullOrWhiteSpace(TaskNote) || !string.IsNullOrWhiteSpace(TaskUrl)));

    public bool ShowInjectTaskPrompt =>
        IsAgent && ((IsCreatingWorkspace && HasNewTaskBinding) || (!IsCreatingWorkspace && SelectedWorkspaceChoice?.Workspace?.HasTask == true));

    /// <summary>The host's agents by name, then "Terminal".</summary>
    public ObservableCollection<string> Kinds { get; } = [];
    [ObservableProperty] public partial int SelectedKind { get; set; }
    public ObservableCollection<string> Models { get; } = [];
    /// <summary>Null: the agent's own default.</summary>
    [ObservableProperty] public partial string? SelectedModel { get; set; }
    public ObservableCollection<string> Efforts { get; } = [];
    [ObservableProperty] public partial int SelectedEffort { get; set; }
    [ObservableProperty] public partial string Prompt { get; set; } = "";
    [ObservableProperty] public partial bool IsModelMenuOpen { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    /// <summary>Why the session could not be started on the host; null when nothing went wrong.</summary>
    [ObservableProperty] public partial string? ErrorText { get; private set; }
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    private AgentOption? Agent => SelectedKind >= 0 && SelectedKind < _host.Agents.Count ? _host.Agents[SelectedKind] : null;
    public bool IsAgent => Agent is not null;
    public bool HasEfforts => Efforts.Count > 0;
    public string ModelText => SelectedModel ?? MobileLangResources.Get(MobileLangResourceKind.NewSessionModelDefault);
    private RemoteProject? Project => Projects.FirstOrDefault(choice => choice.IsSelected)?.Project;

    /// <summary>Opens the sheet on the chosen project (and optionally workspace or new-branch mode) and the host's first agent.</summary>
    public void Open(RemoteProject? project = null, string? workspaceId = null, bool startNewWorkspace = false)
    {
        CancelOptionsLoad();
        _options = RemoteWorkspaceOptions.Empty;
        _optionsProjectId = null;
        ResetNewWorkspaceForm();

        if (BranchModes.Count == 0)
        {
            BranchModes.Add(MobileLangResources.Get(MobileLangResourceKind.WorkspaceModeNew));
            BranchModes.Add(MobileLangResources.Get(MobileLangResourceKind.WorkspaceModeExisting));
        }
        if (TaskModes.Count == 0)
        {
            TaskModes.Add(MobileLangResources.Get(MobileLangResourceKind.WorkspaceTaskNone));
            TaskModes.Add(MobileLangResources.Get(MobileLangResourceKind.WorkspaceTaskIssue));
            TaskModes.Add(MobileLangResources.Get(MobileLangResourceKind.WorkspaceTaskPr));
            TaskModes.Add(MobileLangResources.Get(MobileLangResourceKind.WorkspaceTaskNote));
        }

        Projects.Clear();
        foreach (var candidate in _host.Projects) Projects.Add(new ProjectChoiceViewModel(candidate, p => SelectProject(p, null, false)));
        SelectProject(project ?? _host.Projects.FirstOrDefault(), workspaceId, startNewWorkspace);

        // Away from 0 and back: while the list was empty the control could not select its first entry, and setting the
        // same index again would tell it nothing.
        SelectedKind = -1;
        Kinds.Clear();
        foreach (var agent in _host.Agents) Kinds.Add(agent.Name);
        Kinds.Add(MobileLangResources.Get(MobileLangResourceKind.Terminal));
        SelectedKind = 0;
        Prompt = "";
        ErrorText = null;
        IsOpen = Projects.Count > 0;
    }

    public void Dispose() => CancelOptionsLoad();

    [RelayCommand]
    private void OpenModelMenu() => IsModelMenuOpen = true;

    [RelayCommand]
    private void SelectModel(string? model)
    {
        SelectedModel = model;
        IsModelMenuOpen = false;
    }

    [RelayCommand]
    private void OpenBaseBranchMenu()
    {
        if (BaseBranches.Count > 0) IsBaseBranchMenuOpen = true;
    }

    [RelayCommand]
    private void SelectBaseBranch(string? branch)
    {
        SelectedBaseBranch = branch;
        IsBaseBranchMenuOpen = false;
    }

    [RelayCommand]
    private void OpenExistingBranchMenu()
    {
        if (AvailableExistingBranches.Count > 0) IsExistingBranchMenuOpen = true;
    }

    [RelayCommand]
    private void SelectExistingBranch(RemoteBranchOption? branch)
    {
        SelectedExistingBranch = branch;
        IsExistingBranchMenuOpen = false;
    }

    [RelayCommand]
    private void OpenTaskMenu()
    {
        if (CurrentTaskOptions.Count > 0) IsTaskMenuOpen = true;
    }

    [RelayCommand]
    private void SelectTask(RemoteTaskOption? task)
    {
        SelectedTaskOption = task;
        IsTaskMenuOpen = false;
        if (task is not null && IsNewBranchMode && (!_branchEditedManually || string.IsNullOrWhiteSpace(NewBranchName)))
        {
            _applyingSuggestedBranch = true;
            try { NewBranchName = task.SuggestedBranch; }
            finally { _applyingSuggestedBranch = false; }
        }
        NotifyTaskState();
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (Project is not { } project || IsBusy) return;
        IsBusy = true;
        ErrorText = null;
        try
        {
            var injectTaskPrompt = ShowInjectTaskPrompt && InjectTaskPrompt;
            string? targetWorkspaceId = SelectedWorkspaceChoice?.Workspace?.Id;
            if (IsCreatingWorkspace)
            {
                var branchRequest = BuildCreateWorkspaceRequest();
                if (branchRequest is null)
                {
                    ErrorText = MobileLangResources.Get(MobileLangResourceKind.WorkspaceBranchNameRequired);
                    return;
                }
                var createdWorkspace = await _workspace.CreateWorkspaceAsync(project, branchRequest);
                targetWorkspaceId = createdWorkspace.Id;
                // Keep the newly created workspace selected if starting the session below fails, so retry won't recreate the worktree.
                RebuildWorkspaces(project, createdWorkspace.Id, startNewWorkspace: false);
                InjectTaskPrompt = injectTaskPrompt;
            }

            var agent = Agent;
            var request = new NewSessionRequest(
                agent?.Id,
                agent is null ? null : SelectedModel,
                agent is not null && SelectedEffort >= 0 && SelectedEffort < Efforts.Count ? Efforts[SelectedEffort] : null,
                agent is null || string.IsNullOrWhiteSpace(Prompt) ? null : Prompt,
                targetWorkspaceId,
                injectTaskPrompt);
            var session = await _workspace.CreateSessionAsync(project, request);
            IsOpen = false;
            _created(session);
        }
        catch (IOException e)
        {
            // The host refused or went away: the sheet stays, with what was typed.
            ErrorText = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (!value) CancelOptionsLoad();
    }

    partial void OnSelectedKindChanged(int value)
    {
        ReadAgent();
        OnPropertyChanged(nameof(ShowInjectTaskPrompt));
    }

    partial void OnSelectedModelChanged(string? value) => OnPropertyChanged(nameof(ModelText));

    partial void OnSelectedBranchModeChanged(int value)
    {
        OnPropertyChanged(nameof(IsNewBranchMode));
        OnPropertyChanged(nameof(IsExistingBranchMode));
        OnPropertyChanged(nameof(WorkspacePathPreview));
    }

    partial void OnSelectedBaseBranchChanged(string? value) => OnPropertyChanged(nameof(BaseBranchText));

    partial void OnNewBranchNameChanged(string value)
    {
        if (!_applyingSuggestedBranch) _branchEditedManually = !string.IsNullOrWhiteSpace(value);
        OnPropertyChanged(nameof(WorkspacePathPreview));
    }

    partial void OnSelectedExistingBranchChanged(RemoteBranchOption? value)
    {
        OnPropertyChanged(nameof(ExistingBranchText));
        OnPropertyChanged(nameof(WorkspacePathPreview));
    }

    partial void OnSelectedTaskModeChanged(int value)
    {
        SelectedTaskOption = null;
        RefreshCurrentTaskOptions();
        OnPropertyChanged(nameof(IsRemoteTaskMode));
        OnPropertyChanged(nameof(IsNoteTaskMode));
        NotifyTaskState();
    }

    partial void OnTaskNoteChanged(string value) => NotifyTaskState();
    partial void OnTaskUrlChanged(string value) => NotifyTaskState();
    partial void OnIsLoadingWorkspaceOptionsChanged(bool value) => OnPropertyChanged(nameof(TaskPickerText));
    partial void OnGhAvailableChanged(bool value) => OnPropertyChanged(nameof(TaskPickerText));

    private void SelectProject(RemoteProject? project, string? workspaceId, bool startNewWorkspace)
    {
        foreach (var choice in Projects) choice.IsSelected = ReferenceEquals(choice.Project, project);
        CancelOptionsLoad();
        _options = RemoteWorkspaceOptions.Empty;
        _optionsProjectId = null;
        ResetNewWorkspaceForm();
        RebuildWorkspaces(project, workspaceId, startNewWorkspace);
    }

    private void RebuildWorkspaces(RemoteProject? project, string? workspaceId, bool startNewWorkspace)
    {
        Workspaces.Clear();
        if (project is not null)
        {
            var defaultSubtitle = !string.IsNullOrWhiteSpace(project.DefaultBranch)
                ? $"{project.DefaultBranch} · {project.Path}"
                : project.Path;
            Workspaces.Add(WorkspaceChoiceViewModel.Default(
                MobileLangResources.Get(MobileLangResourceKind.WorkspaceDefaultTitle),
                defaultSubtitle,
                SelectWorkspace));

            foreach (var ws in project.Workspaces)
            {
                var subtitle = ws.HasTask
                    ? string.Join(" · ", new[] { ws.TaskKey, ws.TaskTitle }.Where(s => !string.IsNullOrWhiteSpace(s)))
                    : ws.Path;
                Workspaces.Add(WorkspaceChoiceViewModel.Existing(ws, subtitle, SelectWorkspace));
            }

            if (project.IsGitRepository)
            {
                Workspaces.Add(WorkspaceChoiceViewModel.CreateNew(
                    MobileLangResources.Get(MobileLangResourceKind.WorkspaceNewBranch),
                    $"~/.octopus/workspaces/{WorkspacePaths.SanitizeSegment(project.Name)}/…",
                    SelectWorkspace));
            }
        }

        WorkspaceChoiceViewModel? chosen = null;
        if (startNewWorkspace) chosen = Workspaces.FirstOrDefault(w => w.IsCreateNew);
        if (chosen is null && !string.IsNullOrEmpty(workspaceId)) chosen = Workspaces.FirstOrDefault(w => w.Workspace?.Id == workspaceId);
        chosen ??= Workspaces.FirstOrDefault();

        OnPropertyChanged(nameof(ShowWorkspaces));
        SelectWorkspace(chosen);
    }

    private void SelectWorkspace(WorkspaceChoiceViewModel? choice)
    {
        foreach (var item in Workspaces) item.IsSelected = ReferenceEquals(item, choice);
        InjectTaskPrompt = choice?.IsCreateNew == true;
        OnPropertyChanged(nameof(SelectedWorkspaceChoice));
        OnPropertyChanged(nameof(IsCreatingWorkspace));
        OnPropertyChanged(nameof(WorkspacePathPreview));
        OnPropertyChanged(nameof(ShowInjectTaskPrompt));

        if (choice?.IsCreateNew == true && Project is { } project)
        {
            _ = LoadWorkspaceOptionsAsync(project);
        }
    }

    private async Task LoadWorkspaceOptionsAsync(RemoteProject project)
    {
        if (_optionsProjectId == project.Id && _options.IsGitRepository) return;
        CancelOptionsLoad();
        var cts = _optionsCts = new CancellationTokenSource();
        IsLoadingWorkspaceOptions = true;
        try
        {
            var options = await _workspace.GetWorkspaceOptionsAsync(project, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(Project, project)) return;
            _options = options;
            _optionsProjectId = project.Id;
            ApplyOptions(options);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            if (cts.IsCancellationRequested) return;
            GhAvailable = false;
        }
        finally
        {
            if (ReferenceEquals(_optionsCts, cts)) IsLoadingWorkspaceOptions = false;
        }
    }

    private void ApplyOptions(RemoteWorkspaceOptions options)
    {
        BaseBranches.Clear();
        AvailableExistingBranches.Clear();
        foreach (var branch in options.Branches)
        {
            BaseBranches.Add(branch);
            if (!branch.IsOccupied) AvailableExistingBranches.Add(branch);
        }
        SelectedBaseBranch = options.DefaultBranch
            ?? options.Branches.FirstOrDefault(b => b.IsCurrent)?.Name
            ?? options.Branches.FirstOrDefault(b => !b.IsRemote)?.Name
            ?? options.Branches.FirstOrDefault()?.Name;
        SelectedExistingBranch = AvailableExistingBranches.FirstOrDefault();
        GhAvailable = options.GhAvailable;
        RefreshCurrentTaskOptions();
        OnPropertyChanged(nameof(BaseBranchText));
        OnPropertyChanged(nameof(HasAvailableExistingBranches));
        OnPropertyChanged(nameof(ExistingBranchText));
        OnPropertyChanged(nameof(WorkspacePathPreview));
        OnPropertyChanged(nameof(TaskPickerText));
    }

    private void RefreshCurrentTaskOptions()
    {
        CurrentTaskOptions.Clear();
        var source = SelectedTaskMode switch
        {
            1 => _options.Issues,
            2 => _options.PullRequests,
            _ => [],
        };
        foreach (var item in source) CurrentTaskOptions.Add(item);
        OnPropertyChanged(nameof(HasCurrentTaskOptions));
        OnPropertyChanged(nameof(TaskPickerText));
    }

    private void ResetNewWorkspaceForm()
    {
        SelectedBranchMode = 0;
        BaseBranches.Clear();
        AvailableExistingBranches.Clear();
        SelectedBaseBranch = null;
        SelectedExistingBranch = null;
        _branchEditedManually = false;
        NewBranchName = "";
        SelectedTaskMode = 0;
        CurrentTaskOptions.Clear();
        SelectedTaskOption = null;
        GhAvailable = false;
        TaskNote = "";
        TaskUrl = "";
        InjectTaskPrompt = true;
        IsBaseBranchMenuOpen = false;
        IsExistingBranchMenuOpen = false;
        IsTaskMenuOpen = false;
        OnPropertyChanged(nameof(BaseBranchText));
        OnPropertyChanged(nameof(HasAvailableExistingBranches));
        OnPropertyChanged(nameof(ExistingBranchText));
        OnPropertyChanged(nameof(WorkspacePathPreview));
        NotifyTaskState();
    }

    private CreateBranchWorkspaceRequest? BuildCreateWorkspaceRequest()
    {
        bool createBranch;
        string branchName;
        string? baseRef = null;
        bool isRemote = false;

        if (IsNewBranchMode)
        {
            branchName = NewBranchName.Trim();
            if (branchName.Length == 0) return null;
            createBranch = true;
            baseRef = SelectedBaseBranch;
        }
        else
        {
            if (SelectedExistingBranch is not { } chosen) return null;
            createBranch = false;
            branchName = chosen.Name;
            isRemote = chosen.IsRemote;
        }

        string? taskKind = null, taskKey = null, taskTitle = null, taskUrl = null, taskBody = null;
        if (IsRemoteTaskMode && SelectedTaskOption is { } task)
        {
            taskKind = task.Kind;
            taskKey = task.Key;
            taskTitle = task.Title;
            taskUrl = task.Url;
            taskBody = task.Body;
        }
        else if (IsNoteTaskMode && (!string.IsNullOrWhiteSpace(TaskNote) || !string.IsNullOrWhiteSpace(TaskUrl)))
        {
            var url = string.IsNullOrWhiteSpace(TaskUrl) ? null : TaskUrl.Trim();
            var note = string.IsNullOrWhiteSpace(TaskNote) ? null : TaskNote.Trim();
            taskKind = url is not null ? "url" : "note";
            taskKey = url is not null ? "LINK" : "NOTE";
            taskTitle = note ?? url;
            taskUrl = url;
        }

        return new CreateBranchWorkspaceRequest(createBranch, branchName, baseRef, isRemote, taskKind, taskKey, taskTitle, taskUrl, taskBody);
    }

    private void NotifyTaskState()
    {
        OnPropertyChanged(nameof(TaskPickerText));
        OnPropertyChanged(nameof(ShowInjectTaskPrompt));
    }

    private void CancelOptionsLoad()
    {
        if (_optionsCts is not { } cts) return;
        _optionsCts = null;
        cts.Cancel();
        cts.Dispose();
        IsLoadingWorkspaceOptions = false;
    }

    // The model and effort lists are those of the chosen agent; a terminal has neither.
    private void ReadAgent()
    {
        var agent = Agent;
        Models.Clear();
        Efforts.Clear();
        if (agent is not null)
        {
            foreach (var model in agent.Models) Models.Add(model);
            foreach (var effort in agent.Efforts) Efforts.Add(effort);
        }
        SelectedModel = null;
        SelectedEffort = Efforts.Count == 0 ? -1 : Efforts.Count - 1;
        OnPropertyChanged(nameof(IsAgent));
        OnPropertyChanged(nameof(HasEfforts));
        OnPropertyChanged(nameof(ModelText));
    }
}

public sealed partial class ProjectChoiceViewModel(RemoteProject project, Action<RemoteProject?> select) : ObservableObject
{
    public RemoteProject Project { get; } = project;
    public string Name => Project.Name;
    public string Path => Project.Path;
    [ObservableProperty] public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Choose() => select(Project);
}

public sealed partial class WorkspaceChoiceViewModel : ObservableObject
{
    private readonly Action<WorkspaceChoiceViewModel?> _select;

    private WorkspaceChoiceViewModel(RemoteBranchWorkspace? workspace, bool isDefault, bool isCreateNew, string title, string subtitle, Action<WorkspaceChoiceViewModel?> select)
    {
        Workspace = workspace;
        IsDefault = isDefault;
        IsCreateNew = isCreateNew;
        Title = title;
        Subtitle = subtitle;
        _select = select;
    }

    public static WorkspaceChoiceViewModel Default(string title, string subtitle, Action<WorkspaceChoiceViewModel?> select) =>
        new(null, isDefault: true, isCreateNew: false, title, subtitle, select);

    public static WorkspaceChoiceViewModel Existing(RemoteBranchWorkspace workspace, string subtitle, Action<WorkspaceChoiceViewModel?> select) =>
        new(workspace, isDefault: false, isCreateNew: false, workspace.DisplayName, subtitle, select);

    public static WorkspaceChoiceViewModel CreateNew(string title, string subtitle, Action<WorkspaceChoiceViewModel?> select) =>
        new(null, isDefault: false, isCreateNew: true, title, subtitle, select);

    public RemoteBranchWorkspace? Workspace { get; }
    public bool IsDefault { get; }
    public bool IsCreateNew { get; }
    public bool IsWorktree => Workspace is not null;
    public string Title { get; }
    public string Subtitle { get; }
    [ObservableProperty] public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Choose() => _select(this);
}
