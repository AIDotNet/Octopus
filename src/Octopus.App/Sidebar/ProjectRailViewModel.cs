using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Octopus.App.Activity;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.Core.Projects;
using Octopus.Platform;

namespace Octopus.App.Sidebar;

/// <summary>The 54 px column of project squares and group folders, "+" and settings. Squares and folders can be dragged.</summary>
public sealed partial class ProjectRailViewModel : ObservableObject
{
    private readonly IProjectService _projects;
    private readonly IItemOpener _opener;
    private readonly IDialogService _dialogs;
    private readonly IFileOpener _files;
    private readonly ISettingsNavigator _navigator;
    private readonly IProjectIconStore? _icons;
    private readonly IItemActivityService? _activity;
    /// <summary>The open group as the rail last showed it; null before the first build and when all are closed.</summary>
    private string? _shownExpanded;
    private bool _built;

    public ProjectRailViewModel(
        IProjectService projects,
        IItemOpener opener,
        IDialogService dialogs,
        IFileOpener files,
        ISettingsNavigator navigator,
        IProjectIconStore? icons = null,
        IItemActivityService? activity = null)
    {
        _icons = icons;
        _activity = activity;
        _projects = projects;
        _opener = opener;
        _dialogs = dialogs;
        _files = files;
        _navigator = navigator;
        _projects.Changed += OnProjectsChanged;
        if (_activity is not null)
        {
            // The rail lives as long as the shell, like the activity service: no unsubscribe.
            _activity.Changed += (_, itemId) =>
            {
                if (_projects.FindProjectOf(itemId) is { } project)
                {
                    ShowWork(project);
                }
            };
        }
        Rebuild();
    }

    /// <summary>Every project's square, in or out of a group.</summary>
    public ObservableCollection<ProjectChipViewModel> Chips { get; } = [];

    /// <summary>What the rail shows, top to bottom: the squares of projects in no group, then the groups.</summary>
    public ObservableCollection<object> Entries { get; } = [];

    /// <summary>The colors a group can take, as the group menu offers them (name, value).</summary>
    public static IReadOnlyList<(AppLangResourceKind Name, string Value)> GroupColors { get; } =
    [
        (AppLangResourceKind.ColorIndigo, ProjectService.Palette[0]),
        (AppLangResourceKind.ColorSky, ProjectService.Palette[1]),
        (AppLangResourceKind.ColorEmerald, ProjectService.Palette[2]),
        (AppLangResourceKind.ColorAmber, ProjectService.Palette[3]),
        (AppLangResourceKind.ColorRed, ProjectService.Palette[4]),
        (AppLangResourceKind.ColorPink, ProjectService.Palette[5]),
        (AppLangResourceKind.ColorViolet, ProjectService.Palette[6]),
        (AppLangResourceKind.ColorTeal, ProjectService.Palette[7]),
    ];

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        if (e.Kind is ProjectChangeKind.Projects or ProjectChangeKind.Groups)
        {
            Rebuild();
        }
        else if (e.Kind == ProjectChangeKind.CurrentProject)
        {
            // Another project was chosen: only the rings move. The squares and folders stay as they are.
            var current = _projects.Current?.Id;
            foreach (var chip in Chips)
            {
                chip.IsCurrent = chip.Id == current;
            }
            foreach (var group in Entries.OfType<ProjectGroupViewModel>())
            {
                group.RefreshCurrent();
            }
        }
        else if (e.Kind == ProjectChangeKind.GroupExpanded)
        {
            // In place: the folder views stay and play the change, and the folder that was clicked keeps the focus.
            foreach (var group in Entries.OfType<ProjectGroupViewModel>())
            {
                group.IsExpanded = group.Id == _projects.ExpandedGroupId;
            }
            _shownExpanded = _projects.ExpandedGroupId;
        }
        else if (e.Kind == ProjectChangeKind.Items && e.Project is { } project)
        {
            // A working agent's tab was closed (or one was added).
            ShowWork(project);
        }
    }

    /// <summary>True while any terminal or agent of the project is working on a turn.</summary>
    private bool IsWorking(Project project) =>
        _activity is not null && project.Items.Any(i => _activity.Of(i.Id) == ItemActivity.Working);

    /// <summary>Updates the project's loading ring in place, and the one of the folder it is in.</summary>
    private void ShowWork(Project project)
    {
        if (Chips.FirstOrDefault(c => c.Id == project.Id) is not { } chip)
        {
            return;
        }
        chip.IsWorking = IsWorking(project);
        foreach (var group in Entries.OfType<ProjectGroupViewModel>())
        {
            group.IsWorking = group.Chips.Any(c => c.IsWorking);
        }
    }

    private void Rebuild()
    {
        var current = _projects.Current?.Id;
        var choices = _projects.Groups.Select(g => new GroupChoice(g.Id, g.Name)).ToList();
        var members = _projects.Groups.ToDictionary(g => g.Id, _ => new List<ProjectChipViewModel>());
        Chips.Clear();
        Entries.Clear();
        foreach (var project in _projects.Projects)
        {
            var chip = new ProjectChipViewModel(
                project, project.Id == current, Select, Rename, Reveal, RemoveAsync, ChangeIconAsync, _icons?.Load(project.Image),
                choices, MoveToGroup, NewGroupAsync)
            {
                IsWorking = IsWorking(project),
            };
            Chips.Add(chip);
            if (project.GroupId is { } groupId && members.TryGetValue(groupId, out var list))
            {
                list.Add(chip);
            }
            else
            {
                Entries.Add(chip);
            }
        }
        var expanded = _projects.ExpandedGroupId;
        foreach (var group in _projects.Groups)
        {
            var isExpanded = group.Id == expanded;
            Entries.Add(new ProjectGroupViewModel(
                group, isExpanded, members[group.Id], ToggleGroup, RenameGroupAsync, RemoveGroup, SetGroupColor)
            {
                // A group that opened or closed with this change (a new group opens as it is made) plays it.
                AnimateOnAttach = _built && isExpanded != (group.Id == _shownExpanded),
                IsWorking = members[group.Id].Any(c => c.IsWorking),
            });
        }
        _shownExpanded = expanded;
        _built = true;
    }

    private void Select(ProjectChipViewModel chip) => _projects.SetCurrent(chip.Id);

    private void Rename(ProjectChipViewModel chip)
    {
        _projects.SetCurrent(chip.Id);
        WeakReferenceMessenger.Default.Send(new RenameProjectRequested(chip.Id));
    }

    private void Reveal(ProjectChipViewModel chip) => _files.Reveal(chip.Path);

    private async Task RemoveAsync(ProjectChipViewModel chip)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            AppLangResources.Format(AppLangResourceKind.RemoveProjectTitle, chip.Name),
            AppLangResources.Get(AppLangResourceKind.RemoveProjectBody),
            AppLangResources.Get(AppLangResourceKind.RemoveProject),
            isDanger: true);
        if (confirmed)
        {
            // Closing the items first ends their processes; the dock layout of the project goes with the project.
            var image = _projects.Projects.FirstOrDefault(p => p.Id == chip.Id)?.Image;
            _projects.RemoveProject(chip.Id);
            _icons?.Delete(image);
        }
    }

    private async Task ChangeIconAsync(ProjectChipViewModel chip)
    {
        if (_projects.Projects.FirstOrDefault(p => p.Id == chip.Id) is not { } project)
        {
            return;
        }
        var choice = await _dialogs.AskProjectAppearanceAsync(project);
        if (choice is null)
        {
            return;
        }
        var previous = project.Image;
        _projects.SetProjectAppearance(project.Id, choice.Color, choice.Icon, choice.Image);
        if (previous is not null && previous != choice.Image)
        {
            _icons?.Delete(previous);
        }
    }

    // ---- groups ----

    private void MoveToGroup(ProjectChipViewModel chip, string? groupId) => _projects.MoveProjectToGroup(chip.Id, groupId);

    private Task NewGroupAsync(ProjectChipViewModel chip) => NewGroupWithAsync([chip.Id]);

    /// <summary>Asks for a name, then makes a group of the projects (it opens); nothing happens when the dialog is dismissed.</summary>
    private async Task NewGroupWithAsync(IReadOnlyList<string> projectIds)
    {
        var name = await _dialogs.AskNameAsync(
            AppLangResources.Get(AppLangResourceKind.NewGroupTitle),
            AppLangResources.Get(AppLangResourceKind.GroupNameLabel),
            AppLangResources.Get(AppLangResourceKind.NewGroupDefaultName),
            AppLangResources.Get(AppLangResourceKind.CreateGroup));
        if (name is null)
        {
            return;
        }
        _projects.AddGroup(name.Length > 0 ? name : AppLangResources.Get(AppLangResourceKind.NewGroupDefaultName), projectIds);
    }

    /// <summary>Opens the group, closing the open one; an open group closes.</summary>
    private void ToggleGroup(ProjectGroupViewModel group) => _projects.SetExpandedGroup(group.IsExpanded ? null : group.Id);

    private async Task RenameGroupAsync(ProjectGroupViewModel group)
    {
        var name = await _dialogs.AskNameAsync(
            AppLangResources.Get(AppLangResourceKind.RenameGroupTitle),
            AppLangResources.Get(AppLangResourceKind.GroupNameLabel),
            group.Name,
            AppLangResources.Get(AppLangResourceKind.Save));
        if (!string.IsNullOrWhiteSpace(name))
        {
            _projects.RenameGroup(group.Id, name);
        }
    }

    private void RemoveGroup(ProjectGroupViewModel group) => _projects.RemoveGroup(group.Id);

    private void SetGroupColor(ProjectGroupViewModel group, string color) => _projects.SetGroupColor(group.Id, color);

    // ---- dragging a square ----

    /// <summary>A square dropped on a group's folder or capsule: the project goes into that group.</summary>
    public void DropOnGroup(string projectId, string groupId) => _projects.MoveProjectToGroup(projectId, groupId);

    /// <summary>A square dropped on the rail outside any group: the project leaves its group.</summary>
    public void DropOnRail(string projectId) => _projects.MoveProjectToGroup(projectId, null);

    /// <summary>A square dropped on the upper or lower edge of another: it goes right before or after it, in its group.</summary>
    public void DropProjectNextTo(string projectId, string anchorProjectId, bool after) =>
        _projects.MoveProject(projectId, anchorProjectId, after);

    /// <summary>A folder dropped on the upper or lower half of another: it goes right before or after it.</summary>
    public void DropGroupNextTo(string groupId, string anchorGroupId, bool after) => _projects.MoveGroup(groupId, anchorGroupId, after);

    /// <summary>A folder dropped on the rail below the groups: it goes last.</summary>
    public void DropGroupLast(string groupId)
    {
        if (_projects.Groups.LastOrDefault() is { } last)
        {
            _projects.MoveGroup(groupId, last.Id, after: true);
        }
    }

    /// <summary>
    /// A square dropped on another square: into the other's group when it has one; otherwise the two make a new group
    /// (after asking for its name).
    /// </summary>
    public Task DropOnProjectAsync(string projectId, string targetId)
    {
        var target = _projects.Projects.FirstOrDefault(p => p.Id == targetId);
        if (target is null || projectId == targetId || _projects.Projects.All(p => p.Id != projectId))
        {
            return Task.CompletedTask;
        }
        if (target.GroupId is { } groupId)
        {
            _projects.MoveProjectToGroup(projectId, groupId);
            return Task.CompletedTask;
        }
        return NewGroupWithAsync([targetId, projectId]);
    }

    [RelayCommand]
    private Task NewProjectAsync() => _opener.PickAndAddProjectAsync();

    [RelayCommand]
    private void OpenSettings() => _navigator.OpenSettings();
}
