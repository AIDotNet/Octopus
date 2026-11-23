using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Octopus.Agents;
using Octopus.Agents.Accounts;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.App.Settings;
using Octopus.App.Workspace;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Storage;
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
    private readonly Activity.ISubagentService? _subagents;
    private readonly IAgentAccountService? _accounts;
    private readonly ISettingsStore? _settings;
    /// <summary>Per item, outliving the rows: a rebuild (a filter, a new tab) keeps what the user opened.</summary>
    private readonly Dictionary<string, SubagentListViewModel> _subagentLists = [];
    private bool _updatingName;
    private bool _moving;

    public ProjectPanelViewModel(
        IProjectService projects,
        IItemOpener opener,
        IAgentCatalog catalog,
        IWorkspaceCommands workspace,
        Activity.IItemActivityService activity,
        Activity.ISubagentService? subagents = null,
        IAgentAccountService? accounts = null,
        ISettingsStore? settings = null)
    {
        _subagents = subagents;
        _accounts = accounts;
        _settings = settings;
        if (_accounts is not null) _accounts.Changed += (_, _) => UiContext.Run(Rebuild);
        if (_settings is not null) _settings.Changed += (_, _) => UiContext.Run(Rebuild);
        if (_subagents is not null)
        {
            _subagents.Changed += (_, id) => SyncSubagents(id);
            _subagents.Tick += (_, _) =>
            {
                var now = DateTimeOffset.Now;
                foreach (var list in _subagentLists.Values)
                {
                    list.Tick(now);
                }
            };
        }
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
    /// <summary>True when the project is local and shares working directories with at least one other local project in its group.</summary>
    [ObservableProperty] public partial bool IsGroupMerged { get; private set; }

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
        var group = _projects.Groups.FirstOrDefault(g => g.Id == project?.GroupId);
        GroupName = group?.Name ?? "";
        IsGroupMerged = project is not null
            && group is { MergeWorkspaces: true }
            && ProjectLocation.IsOnThisComputer(project)
            && _projects.Projects.Any(p => p.Id != project.Id && p.GroupId == group.Id && ProjectLocation.IsOnThisComputer(p));
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
                    SubtitleOf(project, item),
                    item.Id == project.ActiveItemId,
                    r => _projects.ActivateItem(r.Id),
                    (r, title) => _projects.RenameItem(r.Id, title),
                    r => _workspace.RestartItemAsync(r.Id),
                    r => _workspace.MoveToNewGroup(r.Id),
                    r => _workspace.CloseItemAsync(r.Id));
                row.Show(_activity.Of(item.Id));
                row.Subagents = _subagentLists.GetValueOrDefault(item.Id);
                Items.Add(row);
            }
        }
        OnPropertyChanged(nameof(NoItems));
        // Lists of items that are gone (closed, or another project's) go with them; a project switch builds them anew.
        foreach (var id in _subagentLists.Keys.Where(id => _projects.FindItem(id) is null).ToList())
        {
            _subagentLists.Remove(id);
        }
    }

    /// <summary>Creates, updates or drops the subagent list of an item, and hands it to the item's row.</summary>
    private void SyncSubagents(string itemId)
    {
        var runs = _subagents!.Of(itemId);
        if (!_subagentLists.TryGetValue(itemId, out var list))
        {
            if (runs.Count == 0)
            {
                return;
            }
            list = new SubagentListViewModel(() => _projects.ActivateItem(itemId));
            _subagentLists[itemId] = list;
        }
        list.Sync(runs, DateTimeOffset.Now);
        if (Items.FirstOrDefault(r => r.Id == itemId) is { } row)
        {
            row.Subagents = list;
        }
    }

    /// <summary>Combines the non-default / multi-account label (if any) with the model's short name.</summary>
    private string? SubtitleOf(Project project, ProjectItem item)
    {
        var model = ModelNameOf(item);
        var account = AccountBadgeOf(project, item);
        if (string.IsNullOrEmpty(account)) return model;
        if (string.IsNullOrEmpty(model)) return account;
        return $"{account} · {model}";
    }

    private string? AccountBadgeOf(Project project, ProjectItem item)
    {
        if (item.Kind != ProjectItemKind.Agent || item.AgentId is null || _accounts is null || _settings is null)
        {
            return null;
        }
        var definition = _catalog.Find(item.AgentId);
        if (definition?.Accounts is null) return null;
        var list = _accounts.GetAccounts(definition);
        if (list.Count < 2) return null;

        var profile = _settings.Current.Agents.ProfileOf(definition.Id);
        var resolved = _accounts.Resolve(definition, profile, project, item.Launch);
        if (resolved.Account is not { } acc) return null;
        return acc.IsDefaultOrigin && acc.Label == "Default"
            ? AppLangResources.Get(AppLangResourceKind.AccountDefaultName)
            : acc.Label;
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
