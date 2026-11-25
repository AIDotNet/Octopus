using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Core.Projects;
using Octopus.Mobile.Common;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Hosts;

/// <summary>Quick filter chips above the project list on <see cref="HostPage"/> (Scheme A).</summary>
public enum HostProjectFilter
{
    All,
    Attention,
    Working,
    WithSessions,
}

/// <summary>
/// One computer's projects as smart collapsible cards (design/ui/移动端项目列表方案.html, Scheme A), with search,
/// status filtering, inline branch-workspace tabs, project quick actions, and the "New" sheet.
/// </summary>
public sealed partial class HostViewModel : ObservableObject, IDisposable
{
    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;
    private readonly Dictionary<string, bool> _expandedOverrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _workspaceSelections = new(StringComparer.Ordinal);

    public HostViewModel(RemoteHost host, IRemoteWorkspace workspace, INavigator navigator)
    {
        Host = host;
        _workspace = workspace;
        _navigator = navigator;
        NewSession = new NewSessionViewModel(host, workspace, navigator.ShowSession);
        Actions = new SessionActionsViewModel(workspace, navigator);
        host.Projects.CollectionChanged += OnProjectsChanged;
        Rebuild();
    }

    public RemoteHost Host { get; }
    public string Title => Host.Name;
    /// <summary>All projects of this host in order.</summary>
    public ObservableCollection<ProjectSectionViewModel> Sections { get; } = [];
    /// <summary>Projects currently shown after applying <see cref="SearchQuery"/> and <see cref="ActiveFilter"/>.</summary>
    public ObservableCollection<ProjectSectionViewModel> VisibleSections { get; } = [];
    public NewSessionViewModel NewSession { get; }
    /// <summary>The long-press menu of a session row, and what it opens.</summary>
    public SessionActionsViewModel Actions { get; }
    [ObservableProperty] public partial bool IsEmpty { get; private set; }
    /// <summary>The "+" menu: a new session in a project, or a new project.</summary>
    [ObservableProperty] public partial bool IsAddMenuOpen { get; set; }
    [ObservableProperty] public partial string SearchQuery { get; set; } = "";
    [ObservableProperty] public partial HostProjectFilter ActiveFilter { get; set; } = HostProjectFilter.All;

    public bool ShowControls => !IsEmpty;
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);
    public bool IsFilteredEmpty => !IsEmpty && VisibleSections.Count == 0;
    public bool IsFilterAll => ActiveFilter == HostProjectFilter.All;
    public bool IsFilterAttention => ActiveFilter == HostProjectFilter.Attention;
    public bool IsFilterWorking => ActiveFilter == HostProjectFilter.Working;
    public bool IsFilterWithSessions => ActiveFilter == HostProjectFilter.WithSessions;

    public int TotalWaitingCount => Sections.Sum(section => section.WaitingCount);
    public int TotalWorkingCount => Sections.Sum(section => section.WorkingCount);
    public int TotalErrorCount => Sections.Sum(section => section.ErrorCount);
    public int TotalAttentionCount => TotalWaitingCount + TotalErrorCount;
    public bool HasWaiting => TotalWaitingCount > 0;
    public bool HasWorking => TotalWorkingCount > 0;
    public bool HasError => TotalErrorCount > 0;
    public bool HasActiveSummary => HasWaiting || HasWorking || HasError;

    public string WaitingSummaryText => MobileLangResources.Format(MobileLangResourceKind.WaitingCount, TotalWaitingCount);
    public string WorkingSummaryText => MobileLangResources.Format(MobileLangResourceKind.WorkingCount, TotalWorkingCount);
    public string ErrorSummaryText => MobileLangResources.Format(MobileLangResourceKind.ErrorCount, TotalErrorCount);
    public string ProjectCountSummaryText => MobileLangResources.Format(MobileLangResourceKind.ProjectCount, Sections.Count);

    public string FilterAllText => $"{MobileLangResources.Get(MobileLangResourceKind.HostFilterAll)} {Sections.Count}";
    public string FilterAttentionText => TotalAttentionCount > 0
        ? $"{MobileLangResources.Get(MobileLangResourceKind.HostFilterAttention)} {TotalAttentionCount}"
        : MobileLangResources.Get(MobileLangResourceKind.HostFilterAttention);
    public string FilterWorkingText => TotalWorkingCount > 0
        ? $"{MobileLangResources.Get(MobileLangResourceKind.HostFilterWorking)} {TotalWorkingCount}"
        : MobileLangResources.Get(MobileLangResourceKind.HostFilterWorking);
    public string FilterWithSessionsText => MobileLangResources.Get(MobileLangResourceKind.HostFilterWithSessions);

    public bool AnyExpanded => VisibleSections.Any(section => section.IsExpanded);
    public string ToggleAllExpandedText => MobileLangResources.Get(AnyExpanded ? MobileLangResourceKind.HostCollapseAll : MobileLangResourceKind.HostExpandAll);

    partial void OnIsEmptyChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowControls));
        OnPropertyChanged(nameof(IsFilteredEmpty));
    }

    partial void OnSearchQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchQuery));
        ApplyFilters();
    }

    partial void OnActiveFilterChanged(HostProjectFilter value)
    {
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterAttention));
        OnPropertyChanged(nameof(IsFilterWorking));
        OnPropertyChanged(nameof(IsFilterWithSessions));
        ApplyFilters();
    }

    [RelayCommand]
    private void Back() => _navigator.Back();

    [RelayCommand]
    private void OpenAddMenu()
    {
        // Without a project there is nothing to start a session in: straight to picking a folder.
        if (Host.Projects.Count == 0) NewProject();
        else IsAddMenuOpen = true;
    }

    [RelayCommand]
    private void OpenNewSession() => NewSession.Open();

    /// <summary>Picks a folder of the host for a new project; once made, the "New" sheet opens on it.</summary>
    [RelayCommand]
    private void NewProject() => _navigator.ShowFolderPicker(Host, project => NewSession.Open(project));

    [RelayCommand]
    private void SelectFilterAll() => ActiveFilter = HostProjectFilter.All;

    [RelayCommand]
    private void SelectFilterAttention() => ActiveFilter = HostProjectFilter.Attention;

    [RelayCommand]
    private void SelectFilterWorking() => ActiveFilter = HostProjectFilter.Working;

    [RelayCommand]
    private void SelectFilterWithSessions() => ActiveFilter = HostProjectFilter.WithSessions;

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    [RelayCommand]
    private void ResetFilters()
    {
        SearchQuery = "";
        ActiveFilter = HostProjectFilter.All;
    }

    [RelayCommand]
    private void ToggleAllExpanded()
    {
        var target = !AnyExpanded;
        foreach (var section in VisibleSections)
        {
            _expandedOverrides[section.Project.Id] = target;
            section.SetExpandedSilently(target);
        }
        NotifyExpansionSummary();
    }

    /// <summary>A row was held: its menu, with "Open" first.</summary>
    public void ShowMenu(RemoteSession session) => Actions.ShowMenu(session, includeOpen: true);

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
            var initialExpanded = DefaultExpandedFor(project);
            _workspaceSelections.TryGetValue(project.Id, out var savedWorkspace);
            var section = new ProjectSectionViewModel(
                project,
                _workspace,
                _navigator,
                _navigator.ShowSession,
                (p, workspaceId, startNewWorkspace) => NewSession.Open(p, workspaceId, startNewWorkspace),
                initialExpanded,
                savedWorkspace,
                OnSectionExpandedChanged,
                OnSectionWorkspaceSelected,
                OnSectionStateChanged);
            Sections.Add(section);
        }
        IsEmpty = Sections.Count == 0;
        ApplyFilters();
        NotifySummaryProperties();
    }

    private bool DefaultExpandedFor(RemoteProject project)
    {
        if (_expandedOverrides.TryGetValue(project.Id, out var saved)) return saved;
        if (project.Sessions.Count == 0) return false;
        var hasActive = project.Sessions.Any(session => session.Activity != SessionActivity.Idle);
        return hasActive || Host.Projects.Count <= 2;
    }

    private void OnSectionExpandedChanged(string projectId, bool expanded)
    {
        _expandedOverrides[projectId] = expanded;
        NotifyExpansionSummary();
    }

    private void OnSectionWorkspaceSelected(string projectId, string? workspaceKey) =>
        _workspaceSelections[projectId] = workspaceKey;

    private void OnSectionStateChanged()
    {
        ApplyFilters();
        NotifySummaryProperties();
    }

    private void ApplyFilters()
    {
        var query = SearchQuery.Trim();
        var filtering = query.Length > 0;
        var matched = new List<ProjectSectionViewModel>(Sections.Count);
        foreach (var section in Sections)
        {
            if (!MatchesFilter(section, ActiveFilter)) continue;
            if (filtering)
            {
                var (projectMatched, sessionMatched) = section.MatchesQuery(query);
                if (!projectMatched && !sessionMatched) continue;
                // Expand temporarily when the search hits a session inside a collapsed project.
                if (sessionMatched && !section.IsExpanded) section.SetExpandedSilently(true);
            }
            else if (!_expandedOverrides.ContainsKey(section.Project.Id))
            {
                section.SetExpandedSilently(DefaultExpandedFor(section.Project));
            }
            matched.Add(section);
        }

        if (!VisibleSections.SequenceEqual(matched, ReferenceEqualityComparer.Instance))
        {
            VisibleSections.Clear();
            foreach (var section in matched) VisibleSections.Add(section);
        }
        OnPropertyChanged(nameof(IsFilteredEmpty));
        NotifyExpansionSummary();
    }

    private static bool MatchesFilter(ProjectSectionViewModel section, HostProjectFilter filter) => filter switch
    {
        HostProjectFilter.Attention => section.HasAttention || section.HasError,
        HostProjectFilter.Working => section.HasWorking,
        HostProjectFilter.WithSessions => section.TotalSessionCount > 0,
        _ => true,
    };

    private void NotifySummaryProperties()
    {
        OnPropertyChanged(nameof(TotalWaitingCount));
        OnPropertyChanged(nameof(TotalWorkingCount));
        OnPropertyChanged(nameof(TotalErrorCount));
        OnPropertyChanged(nameof(TotalAttentionCount));
        OnPropertyChanged(nameof(HasWaiting));
        OnPropertyChanged(nameof(HasWorking));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasActiveSummary));
        OnPropertyChanged(nameof(WaitingSummaryText));
        OnPropertyChanged(nameof(WorkingSummaryText));
        OnPropertyChanged(nameof(ErrorSummaryText));
        OnPropertyChanged(nameof(ProjectCountSummaryText));
        OnPropertyChanged(nameof(FilterAllText));
        OnPropertyChanged(nameof(FilterAttentionText));
        OnPropertyChanged(nameof(FilterWorkingText));
        OnPropertyChanged(nameof(FilterWithSessionsText));
        NotifyExpansionSummary();
    }

    private void NotifyExpansionSummary()
    {
        OnPropertyChanged(nameof(AnyExpanded));
        OnPropertyChanged(nameof(ToggleAllExpandedText));
    }
}

/// <summary>
/// A smart collapsible project card on <see cref="HostPage"/> (Scheme A): shows the project's colored badge, git branch,
/// path, live session status summary, inline branch-workspace filter tabs, session list, and project quick actions.
/// </summary>
public sealed partial class ProjectSectionViewModel : ObservableObject, IDisposable
{
    public const string DefaultWorkspaceKey = "__default__";

    private static readonly string[] FallbackPalette =
    [
        "#5B5BD6", "#1F9D74", "#D97757", "#3B82D6", "#8B5CF6", "#0D9488", "#E07A3B", "#C85A8E",
    ];

    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;
    private readonly Action<RemoteSession> _open;
    private readonly Action<RemoteProject, string?, bool> _openNew;
    private readonly Action<string, bool> _onExpandedChanged;
    private readonly Action<string, string?> _onWorkspaceSelected;
    private readonly Action _onStateChanged;
    private readonly List<RemoteSession> _watchedSessions = [];

    public ProjectSectionViewModel(
        RemoteProject project,
        IRemoteWorkspace workspace,
        INavigator navigator,
        Action<RemoteSession> open,
        Action<RemoteProject, string?, bool> openNew,
        bool initialExpanded,
        string? initialWorkspaceKey,
        Action<string, bool> onExpandedChanged,
        Action<string, string?> onWorkspaceSelected,
        Action onStateChanged)
    {
        Project = project;
        _workspace = workspace;
        _navigator = navigator;
        _open = open;
        _openNew = openNew;
        _onExpandedChanged = onExpandedChanged;
        _onWorkspaceSelected = onWorkspaceSelected;
        _onStateChanged = onStateChanged;
        IsExpanded = initialExpanded;
        SelectedWorkspaceKey = initialWorkspaceKey;
        Initial = InitialOf(project.Name);
        ProjectBrush = BrushOf(project);
        project.Sessions.CollectionChanged += OnChildrenChanged;
        project.Workspaces.CollectionChanged += OnChildrenChanged;
        Rebuild(notifyParent: false);
    }

    public RemoteProject Project { get; }
    public string Name => Project.Name;
    public string Path => Project.Path;
    public string Header => Project.Name + " · " + Project.Path;
    public string Initial { get; }
    public IBrush ProjectBrush { get; }
    public string? DefaultBranch => Project.DefaultBranch;
    public bool HasDefaultBranch => !string.IsNullOrWhiteSpace(Project.DefaultBranch);
    public bool IsGitRepository => Project.IsGitRepository;
    public bool HasWorkspaces => Project.Workspaces.Count > 0;
    public string SubtitleText => HasWorkspaces
        ? $"{Project.Path} · {MobileLangResources.Format(MobileLangResourceKind.ProjectWorkspaceCount, Project.Workspaces.Count)}"
        : Project.Path;

    [ObservableProperty] public partial bool IsExpanded { get; set; }
    /// <summary>Null shows all workspaces; <see cref="DefaultWorkspaceKey"/> shows the project root; otherwise a worktree id.</summary>
    [ObservableProperty] public partial string? SelectedWorkspaceKey { get; set; }

    /// <summary>All sessions of this project in order.</summary>
    public ObservableCollection<SessionRowViewModel> Sessions { get; } = [];
    /// <summary>Populated only when <see cref="HasWorkspaces"/> is false (kept in sync with <see cref="WorkspaceGroups"/>).</summary>
    public ObservableCollection<SessionRowViewModel> FlatSessions { get; } = [];
    /// <summary>Logical workspace buckets when <see cref="HasWorkspaces"/> is true.</summary>
    public ObservableCollection<MobileWorkspaceGroupViewModel> WorkspaceGroups { get; } = [];
    /// <summary>Inline workspace filter chips rendered inside the card when <see cref="HasWorkspaces"/> is true.</summary>
    public ObservableCollection<WorkspaceTabViewModel> WorkspaceTabs { get; } = [];
    /// <summary>The sessions currently rendered inside the expanded card (filtered by <see cref="SelectedWorkspaceKey"/>).</summary>
    public ObservableCollection<SessionRowViewModel> DisplayedSessions { get; } = [];

    public bool HasDisplayedSessions => DisplayedSessions.Count > 0;
    public bool IsDisplayedEmpty => DisplayedSessions.Count == 0;
    public bool ShowWorkspaceTagsOnRows => HasWorkspaces && SelectedWorkspaceKey is null;

    public int WaitingCount { get; private set; }
    public int WorkingCount { get; private set; }
    public int ErrorCount { get; private set; }
    public int TotalSessionCount => Project.Sessions.Count;

    public bool HasAttention => WaitingCount > 0;
    public bool HasError => ErrorCount > 0;
    public bool HasWorking => WorkingCount > 0;
    public bool HasErrorTone => !HasAttention && HasError;

    public bool HasCornerBadge => WaitingCount > 0 || ErrorCount > 0 || WorkingCount > 0;
    public bool CornerIsWaiting => WaitingCount > 0;
    public bool CornerIsError => WaitingCount == 0 && ErrorCount > 0;
    public bool CornerIsWorking => WaitingCount == 0 && ErrorCount == 0 && WorkingCount > 0;
    public string CornerBadgeText => WaitingCount > 0
        ? WaitingCount.ToString()
        : ErrorCount > 0
            ? "!"
            : WorkingCount > 0
                ? WorkingCount.ToString()
                : "";

    public bool SummaryIsWaiting => WaitingCount > 0;
    public bool SummaryIsError => WaitingCount == 0 && ErrorCount > 0;
    public bool SummaryIsWorking => WaitingCount == 0 && ErrorCount == 0 && WorkingCount > 0;
    public bool SummaryIsIdle => !SummaryIsWaiting && !SummaryIsError && !SummaryIsWorking;
    public string SummaryPillText => WaitingCount > 0
        ? MobileLangResources.Format(MobileLangResourceKind.WaitingCount, WaitingCount)
        : ErrorCount > 0
            ? MobileLangResources.Format(MobileLangResourceKind.ErrorCount, ErrorCount)
            : WorkingCount > 0
                ? MobileLangResources.Format(MobileLangResourceKind.WorkingCount, WorkingCount)
                : TotalSessionCount > 0
                    ? MobileLangResources.Format(MobileLangResourceKind.ProjectIdleCount, TotalSessionCount)
                    : MobileLangResources.Format(MobileLangResourceKind.ProjectSessionCount, 0);

    public string ExpandedCountText => TotalSessionCount.ToString();

    public bool CanBrowseFiles => _workspace.SupportsFiles(Project.Host);
    public bool CanViewChanges => Project.IsGitRepository && _workspace.SupportsGit(Project.Host);

    private RemoteBranchWorkspace? SelectedWorkspace =>
        SelectedWorkspaceKey is { Length: > 0 } key && key != DefaultWorkspaceKey
            ? Project.Workspaces.FirstOrDefault(ws => ws.Id == key)
            : null;

    internal void SetExpandedSilently(bool expanded)
    {
        if (IsExpanded == expanded) return;
        IsExpanded = expanded;
    }

    internal (bool ProjectMatched, bool SessionMatched) MatchesQuery(string query)
    {
        var projectMatched =
            Project.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Project.Path.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (Project.DefaultBranch?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            || Project.Workspaces.Any(ws =>
                ws.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (ws.TaskKey?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                || (ws.TaskTitle?.Contains(query, StringComparison.OrdinalIgnoreCase) == true));

        var sessionMatched = Project.Sessions.Any(session =>
            session.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (session.Detail?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            || (session.Preview?.Contains(query, StringComparison.OrdinalIgnoreCase) == true));

        return (projectMatched, sessionMatched);
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        _onExpandedChanged(Project.Id, IsExpanded);
    }

    [RelayCommand]
    private void AddSession() => _openNew(Project, SelectedWorkspace?.Id, false);

    [RelayCommand]
    private void NewBranch() => _openNew(Project, null, true);

    [RelayCommand]
    private void BrowseFiles()
    {
        if (!CanBrowseFiles) return;
        _navigator.ShowFiles(Project, SelectedWorkspace?.Path ?? Project.Path);
    }

    [RelayCommand]
    private void ViewChanges()
    {
        if (!CanViewChanges) return;
        _navigator.ShowChanges(Project, SelectedWorkspace?.Path);
    }

    public void SelectWorkspaceTab(string? workspaceKey)
    {
        if (SelectedWorkspaceKey == workspaceKey) return;
        SelectedWorkspaceKey = workspaceKey;
        _onWorkspaceSelected(Project.Id, workspaceKey);
        foreach (var tab in WorkspaceTabs) tab.IsSelected = tab.Key == workspaceKey;
        RebuildDisplayedSessions();
        OnPropertyChanged(nameof(ShowWorkspaceTagsOnRows));
    }

    public void Dispose()
    {
        Project.Sessions.CollectionChanged -= OnChildrenChanged;
        Project.Workspaces.CollectionChanged -= OnChildrenChanged;
        UnwatchSessions();
        ClearRows();
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild(notifyParent: true);

    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemoteSession.Activity) or nameof(RemoteSession.WorkspaceId) or nameof(RemoteSession.Title) or nameof(RemoteSession.Preview) or "")
        {
            UpdateCounts();
            if (e.PropertyName is nameof(RemoteSession.WorkspaceId))
            {
                Rebuild(notifyParent: true);
                return;
            }
            _onStateChanged();
        }
    }

    private void Rebuild(bool notifyParent)
    {
        UnwatchSessions();
        ClearRows();

        var allRows = new List<(RemoteSession Session, SessionRowViewModel Row)>(Project.Sessions.Count);
        foreach (var session in Project.Sessions)
        {
            session.PropertyChanged += OnSessionPropertyChanged;
            _watchedSessions.Add(session);
            var row = new SessionRowViewModel(session, _open);
            Sessions.Add(row);
            allRows.Add((session, row));
        }

        if (!HasWorkspaces)
        {
            SelectedWorkspaceKey = null;
            foreach (var (_, row) in allRows) FlatSessions.Add(row);
        }
        else
        {
            var knownIds = Project.Workspaces.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
            if (SelectedWorkspaceKey is not null && SelectedWorkspaceKey != DefaultWorkspaceKey && !knownIds.Contains(SelectedWorkspaceKey))
            {
                SelectedWorkspaceKey = null;
                _onWorkspaceSelected(Project.Id, null);
            }

            var defaultRows = allRows
                .Where(x => string.IsNullOrEmpty(x.Session.WorkspaceId) || !knownIds.Contains(x.Session.WorkspaceId))
                .Select(x => x.Row)
                .ToList();
            WorkspaceGroups.Add(new MobileWorkspaceGroupViewModel(Project, null, defaultRows, () => _openNew(Project, null, false)));

            WorkspaceTabs.Add(new WorkspaceTabViewModel(
                key: null,
                label: $"{MobileLangResources.Get(MobileLangResourceKind.WorkspaceAll)} {allRows.Count}",
                isAll: true,
                isDefault: false,
                isWorktree: false,
                isSelected: SelectedWorkspaceKey is null,
                select: SelectWorkspaceTab));

            WorkspaceTabs.Add(new WorkspaceTabViewModel(
                key: DefaultWorkspaceKey,
                label: $"{MobileLangResources.Get(MobileLangResourceKind.WorkspaceDefault)} {defaultRows.Count}",
                isAll: false,
                isDefault: true,
                isWorktree: false,
                isSelected: SelectedWorkspaceKey == DefaultWorkspaceKey,
                select: SelectWorkspaceTab));

            foreach (var ws in Project.Workspaces)
            {
                var wsRows = allRows.Where(x => x.Session.WorkspaceId == ws.Id).Select(x => x.Row).ToList();
                WorkspaceGroups.Add(new MobileWorkspaceGroupViewModel(Project, ws, wsRows, () => _openNew(Project, ws.Id, false)));

                var tabName = ws.HasTask ? ws.TaskKey! : ws.DisplayName;
                WorkspaceTabs.Add(new WorkspaceTabViewModel(
                    key: ws.Id,
                    label: $"{tabName} {wsRows.Count}",
                    isAll: false,
                    isDefault: false,
                    isWorktree: true,
                    isSelected: SelectedWorkspaceKey == ws.Id,
                    select: SelectWorkspaceTab));
            }
        }

        RebuildDisplayedSessions();
        UpdateCounts();
        OnPropertyChanged(nameof(HasWorkspaces));
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(ShowWorkspaceTagsOnRows));
        if (notifyParent) _onStateChanged();
    }

    private void RebuildDisplayedSessions()
    {
        DisplayedSessions.Clear();
        if (!HasWorkspaces || SelectedWorkspaceKey is null)
        {
            foreach (var row in Sessions) DisplayedSessions.Add(row);
        }
        else if (SelectedWorkspaceKey == DefaultWorkspaceKey)
        {
            var knownIds = Project.Workspaces.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var row in Sessions)
            {
                if (string.IsNullOrEmpty(row.Session.WorkspaceId) || !knownIds.Contains(row.Session.WorkspaceId))
                {
                    DisplayedSessions.Add(row);
                }
            }
        }
        else
        {
            foreach (var row in Sessions)
            {
                if (row.Session.WorkspaceId == SelectedWorkspaceKey) DisplayedSessions.Add(row);
            }
        }
        OnPropertyChanged(nameof(HasDisplayedSessions));
        OnPropertyChanged(nameof(IsDisplayedEmpty));
    }

    private void UpdateCounts()
    {
        var waiting = 0;
        var working = 0;
        var error = 0;
        foreach (var session in Project.Sessions)
        {
            switch (session.Activity)
            {
                case SessionActivity.Waiting: waiting++; break;
                case SessionActivity.Working: working++; break;
                case SessionActivity.Error: error++; break;
            }
        }
        WaitingCount = waiting;
        WorkingCount = working;
        ErrorCount = error;

        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(WorkingCount));
        OnPropertyChanged(nameof(ErrorCount));
        OnPropertyChanged(nameof(TotalSessionCount));
        OnPropertyChanged(nameof(HasAttention));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasWorking));
        OnPropertyChanged(nameof(HasErrorTone));
        OnPropertyChanged(nameof(HasCornerBadge));
        OnPropertyChanged(nameof(CornerIsWaiting));
        OnPropertyChanged(nameof(CornerIsError));
        OnPropertyChanged(nameof(CornerIsWorking));
        OnPropertyChanged(nameof(CornerBadgeText));
        OnPropertyChanged(nameof(SummaryIsWaiting));
        OnPropertyChanged(nameof(SummaryIsError));
        OnPropertyChanged(nameof(SummaryIsWorking));
        OnPropertyChanged(nameof(SummaryIsIdle));
        OnPropertyChanged(nameof(SummaryPillText));
        OnPropertyChanged(nameof(ExpandedCountText));
    }

    private void UnwatchSessions()
    {
        foreach (var session in _watchedSessions) session.PropertyChanged -= OnSessionPropertyChanged;
        _watchedSessions.Clear();
    }

    private void ClearRows()
    {
        foreach (var row in Sessions) row.Dispose();
        Sessions.Clear();
        FlatSessions.Clear();
        WorkspaceGroups.Clear();
        WorkspaceTabs.Clear();
        DisplayedSessions.Clear();
    }

    private static string InitialOf(string name) =>
        name.Trim() is { Length: > 0 } trimmed ? char.ToUpperInvariant(trimmed[0]).ToString() : "P";

    private static IBrush BrushOf(RemoteProject project)
    {
        if (!string.IsNullOrWhiteSpace(project.Color) && Color.TryParse(project.Color, out var parsed))
        {
            return new SolidColorBrush(parsed);
        }
        var key = !string.IsNullOrWhiteSpace(project.Id) ? project.Id : project.Name;
        var hash = 17;
        foreach (var ch in key) hash = unchecked(hash * 31 + ch);
        var hex = FallbackPalette[(int)((uint)hash % (uint)FallbackPalette.Length)];
        return new SolidColorBrush(Color.Parse(hex));
    }
}

/// <summary>One selectable workspace chip inside an expanded project card (All, Default folder, or a branch worktree).</summary>
public sealed partial class WorkspaceTabViewModel : ObservableObject
{
    private readonly Action<string?> _select;

    public WorkspaceTabViewModel(string? key, string label, bool isAll, bool isDefault, bool isWorktree, bool isSelected, Action<string?> select)
    {
        Key = key;
        Label = label;
        IsAll = isAll;
        IsDefault = isDefault;
        IsWorktree = isWorktree;
        IsSelected = isSelected;
        _select = select;
    }

    public string? Key { get; }
    public string Label { get; }
    public bool IsAll { get; }
    public bool IsDefault { get; }
    public bool IsWorktree { get; }
    [ObservableProperty] public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Select() => _select(Key);
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
    [ObservableProperty] public partial bool LaunchAsGui { get; set; }
    [ObservableProperty] public partial bool IsModelMenuOpen { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    /// <summary>Why the session could not be started on the host; null when nothing went wrong.</summary>
    [ObservableProperty] public partial string? ErrorText { get; private set; }
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnLaunchAsGuiChanged(bool value) => OnPropertyChanged(nameof(StartButtonText));

    private AgentOption? Agent => SelectedKind >= 0 && SelectedKind < _host.Agents.Count ? _host.Agents[SelectedKind] : null;
    public bool IsAgent => Agent is not null;
    public bool CanLaunchAsGui => Agent is { SupportsAcp: true } && _workspace.SupportsConversations(_host);
    public string LaunchAsGuiSubtitle => MobileLangResources.Get(CanLaunchAsGui
        ? MobileLangResourceKind.NewSessionLaunchGuiHint
        : MobileLangResourceKind.NewSessionLaunchGuiUnsupported);
    public string StartButtonText => MobileLangResources.Get(IsAgent && CanLaunchAsGui && LaunchAsGui
        ? MobileLangResourceKind.NewSessionStartGui
        : MobileLangResourceKind.NewSessionStart);
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
            bool? launchAsGui = agent is null
                ? null
                : CanLaunchAsGui && LaunchAsGui
                    ? true
                    : false;
            var request = new NewSessionRequest(
                agent?.Id,
                agent is null ? null : SelectedModel,
                agent is not null && SelectedEffort >= 0 && SelectedEffort < Efforts.Count ? Efforts[SelectedEffort] : null,
                agent is null || string.IsNullOrWhiteSpace(Prompt) ? null : Prompt,
                targetWorkspaceId,
                injectTaskPrompt,
                launchAsGui);
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
        LaunchAsGui = CanLaunchAsGui && agent?.DefaultGui == true;
        OnPropertyChanged(nameof(IsAgent));
        OnPropertyChanged(nameof(CanLaunchAsGui));
        OnPropertyChanged(nameof(LaunchAsGuiSubtitle));
        OnPropertyChanged(nameof(StartButtonText));
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
