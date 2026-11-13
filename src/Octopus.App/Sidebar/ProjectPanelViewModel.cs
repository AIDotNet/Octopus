using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Octopus.Agents;
using Octopus.App.Common;
using Octopus.App.Services;
using Octopus.App.Settings;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using TKCode.Arc.Localization;

namespace Octopus.App.Sidebar;

/// <summary>The 230 px panel: project name and path, a filter, and the agents, terminals and files of the project.</summary>
public sealed partial class ProjectPanelViewModel : ObservableObject, IRecipient<RenameProjectRequested>
{
    private readonly IProjectService _projects;
    private readonly IItemOpener _opener;
    private readonly IAgentCatalog _catalog;
    private readonly IWorkspaceCommands _workspace;
    private readonly Activity.IItemActivityService _activity;
    private bool _updatingName;
    private bool _moving;

    public ProjectPanelViewModel(
        IProjectService projects,
        IItemOpener opener,
        IAgentCatalog catalog,
        IWorkspaceCommands workspace,
        Activity.IItemActivityService activity)
    {
        _activity = activity;
        _activity.Changed += (_, id) => Items.FirstOrDefault(r => r.Id == id)?.Show(_activity.Of(id));
        _projects = projects;
        _opener = opener;
        _catalog = catalog;
        _workspace = workspace;
        _projects.Changed += OnProjectsChanged;
        // The panel lives as long as the shell, so the static language event needs no unsubscribe.
        ArcLanguage.Changed += (_, _) => UiContext.Run(Rebuild);
        WeakReferenceMessenger.Default.Register(this);
        Rebuild();
    }

    /// <summary>The project's terminals and agents in one list, in the order the user arranged them.</summary>
    public ObservableCollection<ItemRowViewModel> Items { get; } = [];

    [ObservableProperty] public partial bool HasProject { get; private set; }
    [ObservableProperty] public partial string ProjectName { get; set; } = "";
    [ObservableProperty] public partial string ProjectPath { get; private set; } = "";
    [ObservableProperty] public partial string Filter { get; set; } = "";
    /// <summary>The name of the group the project is in; empty when it is in none.</summary>
    [ObservableProperty] public partial string GroupName { get; private set; } = "";

    public bool HasNoProject => !HasProject;
    public bool HasGroup => GroupName.Length > 0;
    public bool NoItems => Items.Count == 0;

    /// <summary>Asks the view to put the project name into editing.</summary>
    public event EventHandler? RenameRequested;

    partial void OnHasProjectChanged(bool value) => OnPropertyChanged(nameof(HasNoProject));

    partial void OnGroupNameChanged(string value) => OnPropertyChanged(nameof(HasGroup));

    partial void OnFilterChanged(string value) => Rebuild();

    partial void OnProjectNameChanged(string value)
    {
        if (!_updatingName && _projects.Current is { } project)
        {
            _projects.RenameProject(project.Id, value);
        }
    }

    public void Receive(RenameProjectRequested message) => RenameRequested?.Invoke(this, EventArgs.Empty);

    private void OnProjectsChanged(object? sender, ProjectChangedEventArgs e)
    {
        // A session renames its item as it goes: the row is updated in place, so a rename being typed in another
        // row, or a drag, is not interrupted by a rebuild. With a filter the row may have to appear or go.
        if (e.Kind == ProjectChangeKind.ItemTitle && Filter.Trim().Length == 0 && e.Item is { } renamed
            && Items.FirstOrDefault(r => r.Id == renamed.Id) is { } row)
        {
            row.RefreshTitle();
            return;
        }
        // Another tab was clicked: only the highlight moves. The rows (and their views) stay, so a tab click does not
        // rebuild the whole list.
        if (e.Kind == ProjectChangeKind.Active)
        {
            var active = _projects.Current?.ActiveItemId;
            foreach (var item in Items)
            {
                item.IsActive = item.Id == active;
            }
            return;
        }
        if (e.Kind is not (ProjectChangeKind.Layout or ProjectChangeKind.GroupExpanded or ProjectChangeKind.Tunnels) && !_moving)
        {
            Rebuild();
        }
    }

    [RelayCommand]
    private Task NewProjectAsync() => _opener.PickAndAddProjectAsync();

    /// <summary>
    /// Saves a move the list already made in <see cref="Items"/>: the row now at <paramref name="newIndex"/> came from
    /// <paramref name="oldIndex"/>. The saved order follows the row's new neighbour, so it also holds while a filter hides rows.
    /// </summary>
    public void CommitMove(int oldIndex, int newIndex)
    {
        if (oldIndex == newIndex || newIndex < 0 || newIndex >= Items.Count || Items.Count < 2)
        {
            return;
        }
        var down = newIndex > oldIndex;
        var anchor = Items[down ? newIndex - 1 : newIndex + 1];
        _moving = true;
        try
        {
            _projects.MoveItem(Items[newIndex].Id, anchor.Id, after: down);
        }
        finally
        {
            _moving = false;
        }
    }

    private void Rebuild()
    {
        var project = _projects.Current;
        HasProject = project is not null;
        _updatingName = true;
        ProjectName = project?.Name ?? "";
        _updatingName = false;
        ProjectPath = project?.Path ?? "";
        GroupName = _projects.Groups.FirstOrDefault(g => g.Id == project?.GroupId)?.Name ?? "";
        Items.Clear();
        if (project is not null)
        {
            var filter = Filter.Trim();
            foreach (var item in project.Items)
            {
                // Open files are tabs only; the panel lists what runs (agents and terminals).
                if (item.Kind is not (ProjectItemKind.Agent or ProjectItemKind.Terminal))
                {
                    continue;
                }
                if (filter.Length > 0 && !item.DisplayTitle.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var row = new ItemRowViewModel(
                    item,
                    ModelNameOf(item),
                    item.Id == project.ActiveItemId,
                    r => _projects.ActivateItem(r.Id),
                    (r, title) => _projects.RenameItem(r.Id, title),
                    r => _workspace.RestartItemAsync(r.Id),
                    r => _workspace.MoveToNewGroup(r.Id),
                    r => _workspace.CloseItemAsync(r.Id));
                row.Show(_activity.Of(item.Id));
                Items.Add(row);
            }
        }
        OnPropertyChanged(nameof(NoItems));
    }

    /// <summary>The model's short name: its label without the agent's own name in front ("Fable 5.2"), or the custom id.</summary>
    private string? ModelNameOf(ProjectItem item)
    {
        var model = item.Launch?.Model;
        if (item.Kind != ProjectItemKind.Agent || string.IsNullOrEmpty(model))
        {
            return null;
        }
        var definition = item.AgentId is null ? null : _catalog.Find(item.AgentId);
        var label = definition?.Capabilities.Model?.Options.FirstOrDefault(o => o.Id == model)?.LabelFor(AgentLanguage.Current) ?? model;
        var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var first = definition?.Name.Split(' ')[0];
        return words.Length > 1 && first is not null && string.Equals(words[0], first, StringComparison.OrdinalIgnoreCase)
            ? string.Join(' ', words.Skip(1))
            : label;
    }
}
