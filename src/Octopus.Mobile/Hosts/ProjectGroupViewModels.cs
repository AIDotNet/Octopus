using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Common;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Hosts;

/// <summary>One chip in the horizontal Desktop Project Groups quick-filter bar on <see cref="HostPage"/>.</summary>
public sealed partial class ProjectGroupFilterChipViewModel : ObservableObject
{
    private readonly Action<string?> _select;
    private string? _lastColorHex;

    public ProjectGroupFilterChipViewModel(
        string? key,
        string label,
        int count,
        string? colorHex,
        bool isMerged,
        bool isSelected,
        Action<string?> select)
    {
        Key = key;
        _select = select;
        Update(label, count, colorHex, isMerged, isSelected);
    }

    public string? Key { get; }
    [ObservableProperty] public partial string Label { get; private set; } = "";
    [ObservableProperty] public partial string CountText { get; private set; } = "0";
    [ObservableProperty] public partial bool HasColorDot { get; private set; }
    [ObservableProperty] public partial IBrush ColorBrush { get; private set; } = Brushes.Transparent;
    [ObservableProperty] public partial bool IsMerged { get; private set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }

    internal void Update(string label, int count, string? colorHex, bool isMerged, bool isSelected)
    {
        Label = label;
        CountText = count.ToString();
        var hasDot = !string.IsNullOrWhiteSpace(colorHex);
        HasColorDot = hasDot;
        if (!string.Equals(_lastColorHex, colorHex, StringComparison.OrdinalIgnoreCase))
        {
            _lastColorHex = colorHex;
            ColorBrush = hasDot ? new SolidColorBrush(ProjectGroup2x2View.ParseGroupColor(colorHex)) : Brushes.Transparent;
        }
        IsMerged = isMerged;
        IsSelected = isSelected;
    }

    [RelayCommand]
    private void Select() => _select(Key);
}

/// <summary>
/// One Desktop Project Group capsule on <see cref="HostPage"/>:
/// collapsed renders Desktop's 2×2 preview square + aggregated status pill WITHOUT mounting child project controls;
/// expanded renders a group-tinted capsule with open-folder icon, optional <c>MergeWorkspaces</c> shared-dirs bar,
/// and lazily mounted member project cards. Reused in-place across session/status updates to avoid visual-tree churn.
/// </summary>
public sealed partial class ProjectGroupCardViewModel : ObservableObject
{
    private readonly Action<string, bool> _onExpandedChanged;
    private readonly Action<RemoteProjectGroup> _manage;
    private readonly Action<string?> _focusGroup;
    private List<ProjectSectionViewModel> _allMembers = [];
    private string? _lastColorHex;
    private bool _colorInitialized;

    public ProjectGroupCardViewModel(
        RemoteProjectGroup group,
        IReadOnlyList<ProjectSectionViewModel> memberSections,
        bool initialExpanded,
        bool canManage,
        Action<string, bool> onExpandedChanged,
        Action<RemoteProjectGroup> manage,
        Action<string?> focusGroup)
    {
        Group = group;
        _onExpandedChanged = onExpandedChanged;
        _manage = manage;
        _focusGroup = focusGroup;
        Update(memberSections, canManage, initialExpanded);
    }

    public RemoteProjectGroup Group { get; }
    public string Name => Group.Name;
    public string? GroupColor => Group.Color;
    public bool IsMerged => Group.MergeWorkspaces;
    [ObservableProperty] public partial bool CanManage { get; private set; }
    [ObservableProperty] public partial string CountText { get; private set; } = "0";
    [ObservableProperty] public partial bool HasMembers { get; private set; }
    [ObservableProperty] public partial bool IsEmptyGroup { get; private set; }
    [ObservableProperty] public partial IReadOnlyList<RemoteProject> MemberProjects { get; private set; } = [];
    public IReadOnlyList<ProjectSectionViewModel> AllMembers => _allMembers;
    [ObservableProperty] public partial string MemberPreviewText { get; private set; } = "";
    [ObservableProperty] public partial string MergeBarText { get; private set; } = "";

    [ObservableProperty] public partial IBrush TintBrush { get; private set; } = Brushes.Transparent;
    [ObservableProperty] public partial IBrush OpenFolderBg { get; private set; } = Brushes.Transparent;
    [ObservableProperty] public partial IBrush ExpandedCapsuleBg { get; private set; } = Brushes.Transparent;
    [ObservableProperty] public partial IBrush ExpandedCapsuleBorder { get; private set; } = Brushes.Transparent;
    [ObservableProperty] public partial IBrush CollapsedCapsuleBorder { get; private set; } = Brushes.Transparent;
    [ObservableProperty] public partial IBrush MergeBarBg { get; private set; } = Brushes.Transparent;
    [ObservableProperty] public partial IBrush MergeBarBorder { get; private set; } = Brushes.Transparent;

    public IBrush ActiveBorderBrush => IsExpanded ? ExpandedCapsuleBorder : CollapsedCapsuleBorder;

    [ObservableProperty] public partial int WaitingCount { get; private set; }
    [ObservableProperty] public partial int WorkingCount { get; private set; }
    [ObservableProperty] public partial int ErrorCount { get; private set; }
    [ObservableProperty] public partial bool HasWaiting { get; private set; }
    [ObservableProperty] public partial bool HasError { get; private set; }
    [ObservableProperty] public partial bool HasWorking { get; private set; }
    [ObservableProperty] public partial bool StatusIsWaiting { get; private set; }
    [ObservableProperty] public partial bool StatusIsError { get; private set; }
    [ObservableProperty] public partial bool StatusIsWorking { get; private set; }
    [ObservableProperty] public partial bool StatusIsIdle { get; private set; }
    [ObservableProperty] public partial string StatusPillText { get; private set; } = "";

    /// <summary>
    /// Lazily mounted member sections: empty while collapsed so Avalonia never instantiates child project cards
    /// or session rows for collapsed groups (prevents visual-tree bloat and background wakeup freezes).
    /// </summary>
    public ObservableCollection<ProjectSectionViewModel> MountedSections { get; } = [];

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    /// <summary>Updates this group card in-place without tearing down mounted project cards or 2×2 preview controls.</summary>
    internal void Update(IReadOnlyList<ProjectSectionViewModel> memberSections, bool canManage, bool expanded)
    {
        _allMembers = [.. memberSections];
        CanManage = canManage;

        if (!_colorInitialized || !string.Equals(_lastColorHex, Group.Color, StringComparison.OrdinalIgnoreCase))
        {
            _colorInitialized = true;
            _lastColorHex = Group.Color;
            var baseColor = ProjectGroup2x2View.ParseGroupColor(Group.Color);
            TintBrush = new SolidColorBrush(baseColor);
            OpenFolderBg = new SolidColorBrush(Color.FromArgb(46, baseColor.R, baseColor.G, baseColor.B));
            ExpandedCapsuleBg = new SolidColorBrush(Color.FromArgb(22, baseColor.R, baseColor.G, baseColor.B));
            ExpandedCapsuleBorder = new SolidColorBrush(Color.FromArgb(92, baseColor.R, baseColor.G, baseColor.B));
            CollapsedCapsuleBorder = new SolidColorBrush(Color.FromArgb(58, baseColor.R, baseColor.G, baseColor.B));
            MergeBarBg = new SolidColorBrush(Color.FromArgb(26, baseColor.R, baseColor.G, baseColor.B));
            MergeBarBorder = new SolidColorBrush(Color.FromArgb(74, baseColor.R, baseColor.G, baseColor.B));
            OnPropertyChanged(nameof(GroupColor));
            OnPropertyChanged(nameof(ActiveBorderBrush));
        }

        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IsMerged));

        var nextProjects = memberSections.Select(s => s.Project).ToList();
        if (!MemberProjects.SequenceEqual(nextProjects))
        {
            MemberProjects = nextProjects;
        }
        CountText = memberSections.Count.ToString();
        HasMembers = memberSections.Count > 0;
        IsEmptyGroup = memberSections.Count == 0;
        MemberPreviewText = memberSections.Count > 0
            ? string.Join(" · ", memberSections.Select(s => s.Name))
            : MobileLangResources.Get(MobileLangResourceKind.ProjectGroupEmptyHint);
        MergeBarText = MobileLangResources.Format(
            MobileLangResourceKind.ProjectGroupMergeActiveBar,
            memberSections.Count,
            memberSections.Count > 0 ? string.Join(" + ", memberSections.Select(s => s.Name)) : "—");

        WaitingCount = memberSections.Sum(s => s.WaitingCount);
        WorkingCount = memberSections.Sum(s => s.WorkingCount);
        ErrorCount = memberSections.Sum(s => s.ErrorCount);
        HasWaiting = WaitingCount > 0;
        HasError = ErrorCount > 0;
        HasWorking = WorkingCount > 0;
        StatusIsWaiting = HasWaiting;
        StatusIsError = !HasWaiting && HasError;
        StatusIsWorking = !HasWaiting && !HasError && HasWorking;
        StatusIsIdle = !StatusIsWaiting && !StatusIsError && !StatusIsWorking;
        StatusPillText = StatusIsWaiting
            ? MobileLangResources.Format(MobileLangResourceKind.WaitingCount, WaitingCount)
            : StatusIsError
                ? MobileLangResources.Format(MobileLangResourceKind.ErrorCount, ErrorCount)
                : StatusIsWorking
                    ? MobileLangResources.Format(MobileLangResourceKind.WorkingCount, WorkingCount)
                    : MobileLangResources.Get(MobileLangResourceKind.Idle);

        SetExpandedSilently(expanded);
        SyncMountedSections();
    }

    partial void OnIsExpandedChanged(bool value)
    {
        SyncMountedSections();
        OnPropertyChanged(nameof(ActiveBorderBrush));
    }

    internal void SetExpandedSilently(bool expanded)
    {
        if (IsExpanded == expanded) return;
        IsExpanded = expanded;
    }

    private void SyncMountedSections()
    {
        if (!IsExpanded)
        {
            if (MountedSections.Count > 0) MountedSections.Clear();
            return;
        }
        MobileCollectionSync.SyncByReference(MountedSections, _allMembers);
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        _onExpandedChanged(Group.Id, IsExpanded);
    }

    [RelayCommand]
    private void Manage() => _manage(Group);

    [RelayCommand]
    private void FocusGroup() => _focusGroup(Group.Id);
}

public sealed partial class ProjectGroupColorSwatchViewModel : ObservableObject
{
    private readonly Action<string> _select;

    public ProjectGroupColorSwatchViewModel(string hex, bool isSelected, Action<string> select)
    {
        Hex = hex;
        Brush = new SolidColorBrush(Color.Parse(hex));
        IsSelected = isSelected;
        _select = select;
    }

    public string Hex { get; }
    public IBrush Brush { get; }
    [ObservableProperty] public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Select() => _select(Hex);
}

public sealed partial class ProjectGroupMemberToggleViewModel : ObservableObject
{
    private readonly Func<RemoteProject, Task> _toggle;

    public ProjectGroupMemberToggleViewModel(RemoteProject project, bool isInGroup, string subtitle, Func<RemoteProject, Task> toggle)
    {
        Project = project;
        IsInGroup = isInGroup;
        Subtitle = subtitle;
        _toggle = toggle;
    }

    public RemoteProject Project { get; }
    public string Name => Project.Name;
    [ObservableProperty] public partial bool IsInGroup { get; set; }
    [ObservableProperty] public partial string Subtitle { get; set; }

    public string ActionLabel => MobileLangResources.Get(
        IsInGroup ? MobileLangResourceKind.ProjectGroupMemberInGroup : MobileLangResourceKind.ProjectGroupMemberAdd);

    partial void OnIsInGroupChanged(bool value) => OnPropertyChanged(nameof(ActionLabel));

    [RelayCommand]
    private Task ToggleAsync() => _toggle(Project);
}

/// <summary>Drives Manage Project Group, New Project Group, Dissolve Confirmation, and per-project Move-to-Group sheets on <see cref="HostPage"/>.</summary>
public sealed partial class ProjectGroupEditorViewModel : ObservableObject
{
    /// <summary>Matches Desktop's exact 8-color ProjectService.Palette (Indigo, Sky, Emerald, Amber, Red, Pink, Violet, Teal).</summary>
    public static readonly string[] Palette =
    [
        "#6366F1", "#0EA5E9", "#10B981", "#F59E0B", "#EF4444", "#EC4899", "#8B5CF6", "#14B8A6",
    ];

    private readonly RemoteHost _host;
    private readonly IRemoteWorkspace _workspace;
    private readonly Action _afterMutated;
    private readonly HashSet<string> _selectedNewMemberIds = new(StringComparer.Ordinal);

    public ProjectGroupEditorViewModel(RemoteHost host, IRemoteWorkspace workspace, Action afterMutated)
    {
        _host = host;
        _workspace = workspace;
        _afterMutated = afterMutated;

        foreach (var hex in Palette)
        {
            EditSwatches.Add(new ProjectGroupColorSwatchViewModel(hex, false, SelectEditColor));
            NewSwatches.Add(new ProjectGroupColorSwatchViewModel(hex, hex == Palette[0], SelectNewColor));
        }
    }

    // Manage existing group sheet
    [ObservableProperty] public partial bool IsManageOpen { get; set; }
    [ObservableProperty] public partial bool IsDissolveConfirmOpen { get; set; }
    [ObservableProperty] public partial RemoteProjectGroup? EditingGroup { get; private set; }
    [ObservableProperty] public partial string EditName { get; set; } = "";
    [ObservableProperty] public partial string EditColor { get; set; } = Palette[0];
    [ObservableProperty] public partial bool EditMergeWorkspaces { get; set; }
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }

    public ObservableCollection<ProjectGroupColorSwatchViewModel> EditSwatches { get; } = [];
    public ObservableCollection<ProjectGroupMemberToggleViewModel> MemberToggles { get; } = [];
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string ManageTitle => EditingGroup is { } g
        ? $"{MobileLangResources.Get(MobileLangResourceKind.ProjectGroupManageTitle)} · {g.Name}"
        : MobileLangResources.Get(MobileLangResourceKind.ProjectGroupManageTitle);
    public string DissolveConfirmBody => EditingGroup is { } g
        ? MobileLangResources.Format(MobileLangResourceKind.ProjectGroupDissolveBody, g.Name)
        : "";

    // Create new group sheet (with optional initial member project toggles)
    [ObservableProperty] public partial bool IsNewOpen { get; set; }
    [ObservableProperty] public partial string NewName { get; set; } = "";
    [ObservableProperty] public partial string NewColor { get; set; } = Palette[0];
    [ObservableProperty] public partial bool NewMergeWorkspaces { get; set; }
    public ObservableCollection<ProjectGroupColorSwatchViewModel> NewSwatches { get; } = [];
    public ObservableCollection<ProjectGroupMemberToggleViewModel> NewMemberToggles { get; } = [];
    public bool HasNewMemberChoices => NewMemberToggles.Count > 0;

    // Quick per-project "Move to group" ActionSheet
    [ObservableProperty] public partial bool IsMoveSheetOpen { get; set; }
    [ObservableProperty] public partial RemoteProject? MoveProjectTarget { get; private set; }
    public string MoveSheetTitle => MoveProjectTarget is { } p
        ? MobileLangResources.Format(MobileLangResourceKind.ProjectGroupMoveTitle, p.Name)
        : MobileLangResources.Get(MobileLangResourceKind.ProjectGroupMoveAction);

    /// <summary>Raised when <see cref="MoveProjectTarget"/> or groups change so the page refreshes the Move ActionSheet rows.</summary>
    public event Action? MoveSheetChanged;

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnEditingGroupChanged(RemoteProjectGroup? value)
    {
        OnPropertyChanged(nameof(ManageTitle));
        OnPropertyChanged(nameof(DissolveConfirmBody));
    }
    partial void OnMoveProjectTargetChanged(RemoteProject? value)
    {
        OnPropertyChanged(nameof(MoveSheetTitle));
        MoveSheetChanged?.Invoke();
    }

    public void OpenManage(RemoteProjectGroup group)
    {
        EditingGroup = group;
        EditName = group.Name;
        EditColor = string.IsNullOrWhiteSpace(group.Color) ? Palette[0] : group.Color;
        EditMergeWorkspaces = group.MergeWorkspaces;
        ErrorMessage = null;
        IsDissolveConfirmOpen = false;
        foreach (var sw in EditSwatches) sw.IsSelected = string.Equals(sw.Hex, EditColor, StringComparison.OrdinalIgnoreCase);
        RebuildMemberToggles(group);
        IsManageOpen = true;
    }

    public void OpenNew(RemoteProject? preselectProject = null)
    {
        NewName = "";
        NewColor = Palette[_host.ProjectGroups.Count % Palette.Length];
        NewMergeWorkspaces = false;
        ErrorMessage = null;
        _selectedNewMemberIds.Clear();
        if (preselectProject is not null) _selectedNewMemberIds.Add(preselectProject.Id);
        foreach (var sw in NewSwatches) sw.IsSelected = string.Equals(sw.Hex, NewColor, StringComparison.OrdinalIgnoreCase);
        RebuildNewMemberToggles();
        IsNewOpen = true;
    }

    public void OpenMoveProject(RemoteProject project)
    {
        ErrorMessage = null;
        MoveProjectTarget = project;
        MoveSheetChanged?.Invoke();
        IsMoveSheetOpen = true;
    }

    private void SelectEditColor(string hex)
    {
        EditColor = hex;
        foreach (var sw in EditSwatches) sw.IsSelected = string.Equals(sw.Hex, hex, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectNewColor(string hex)
    {
        NewColor = hex;
        foreach (var sw in NewSwatches) sw.IsSelected = string.Equals(sw.Hex, hex, StringComparison.OrdinalIgnoreCase);
    }

    private void RebuildMemberToggles(RemoteProjectGroup group)
    {
        MemberToggles.Clear();
        foreach (var project in _host.Projects)
        {
            var inGroup = string.Equals(project.GroupId, group.Id, StringComparison.Ordinal);
            var subtitle = inGroup
                ? MobileLangResources.Get(MobileLangResourceKind.ProjectGroupMemberInGroup)
                : !string.IsNullOrWhiteSpace(project.GroupId)
                    && _host.ProjectGroups.FirstOrDefault(g => string.Equals(g.Id, project.GroupId, StringComparison.Ordinal)) is { } other
                        ? MobileLangResources.Format(MobileLangResourceKind.ProjectGroupMemberOther, other.Name)
                        : MobileLangResources.Get(MobileLangResourceKind.ProjectGroupMemberNone);
            MemberToggles.Add(new ProjectGroupMemberToggleViewModel(project, inGroup, subtitle, ToggleMemberAsync));
        }
    }

    private void RebuildNewMemberToggles()
    {
        NewMemberToggles.Clear();
        foreach (var project in _host.Projects)
        {
            var selected = _selectedNewMemberIds.Contains(project.Id);
            var subtitle = !string.IsNullOrWhiteSpace(project.GroupId)
                && _host.ProjectGroups.FirstOrDefault(g => string.Equals(g.Id, project.GroupId, StringComparison.Ordinal)) is { } other
                    ? MobileLangResources.Format(MobileLangResourceKind.ProjectGroupMemberOther, other.Name)
                    : MobileLangResources.Get(MobileLangResourceKind.ProjectGroupMemberNone);
            NewMemberToggles.Add(new ProjectGroupMemberToggleViewModel(project, selected, subtitle, ToggleNewMemberAsync));
        }
        OnPropertyChanged(nameof(HasNewMemberChoices));
    }

    private Task ToggleNewMemberAsync(RemoteProject project)
    {
        if (!_selectedNewMemberIds.Add(project.Id)) _selectedNewMemberIds.Remove(project.Id);
        if (NewMemberToggles.FirstOrDefault(t => ReferenceEquals(t.Project, project)) is { } item)
        {
            item.IsInGroup = _selectedNewMemberIds.Contains(project.Id);
        }
        return Task.CompletedTask;
    }

    private async Task ToggleMemberAsync(RemoteProject project)
    {
        if (EditingGroup is not { } group || IsBusy) return;
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var nextGroupId = string.Equals(project.GroupId, group.Id, StringComparison.Ordinal) ? null : group.Id;
            await _workspace.SetProjectGroupAsync(project, nextGroupId);
            RebuildMemberToggles(group);
            _afterMutated();
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task MoveProjectToGroupAsync(string? groupId)
    {
        if (MoveProjectTarget is not { } project || IsBusy) return;
        IsMoveSheetOpen = false;
        if (string.Equals(project.GroupId, groupId, StringComparison.Ordinal)) return;
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await _workspace.SetProjectGroupAsync(project, groupId);
            _afterMutated();
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void MoveProjectToNewGroup()
    {
        var project = MoveProjectTarget;
        IsMoveSheetOpen = false;
        OpenNew(project);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (EditingGroup is not { } group || IsBusy) return;
        var name = EditName.Trim();
        if (name.Length == 0) return;
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await _workspace.UpdateProjectGroupAsync(group, name: name, color: EditColor, mergeWorkspaces: EditMergeWorkspaces);
            IsManageOpen = false;
            _afterMutated();
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RequestDissolve()
    {
        if (EditingGroup is null || IsBusy) return;
        IsDissolveConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelDissolve() => IsDissolveConfirmOpen = false;

    [RelayCommand]
    private async Task DissolveAsync()
    {
        if (EditingGroup is not { } group || IsBusy) return;
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            await _workspace.DeleteProjectGroupAsync(group);
            IsDissolveConfirmOpen = false;
            IsManageOpen = false;
            EditingGroup = null;
            _afterMutated();
        }
        catch (IOException e)
        {
            IsDissolveConfirmOpen = false;
            ErrorMessage = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (IsBusy) return;
        var name = NewName.Trim();
        if (name.Length == 0) name = $"{MobileLangResources.Get(MobileLangResourceKind.ProjectGroupNewShort)} {_host.ProjectGroups.Count + 1}";
        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var created = await _workspace.CreateProjectGroupAsync(_host, name, NewColor, NewMergeWorkspaces);
            if (_selectedNewMemberIds.Count > 0)
            {
                foreach (var project in _host.Projects.ToList())
                {
                    if (_selectedNewMemberIds.Contains(project.Id))
                    {
                        await _workspace.SetProjectGroupAsync(project, created.Id);
                    }
                }
            }
            _selectedNewMemberIds.Clear();
            IsNewOpen = false;
            _afterMutated();
        }
        catch (IOException e)
        {
            ErrorMessage = e.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
