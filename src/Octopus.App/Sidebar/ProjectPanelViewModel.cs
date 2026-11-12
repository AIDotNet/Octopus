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
    private bool _updatingName;

    public ProjectPanelViewModel(
        IProjectService projects,
        IItemOpener opener,
        IAgentCatalog catalog,
        IWorkspaceCommands workspace)
    {
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

    public ObservableCollection<ItemRowViewModel> Agents { get; } = [];
    public ObservableCollection<ItemRowViewModel> Terminals { get; } = [];
    public ObservableCollection<ItemRowViewModel> Files { get; } = [];

    [ObservableProperty] public partial bool HasProject { get; private set; }
    [ObservableProperty] public partial string ProjectName { get; set; } = "";
    [ObservableProperty] public partial string ProjectPath { get; private set; } = "";
    [ObservableProperty] public partial string Filter { get; set; } = "";

    public bool HasNoProject => !HasProject;
    public bool NoAgents => Agents.Count == 0;
    public bool NoTerminals => Terminals.Count == 0;
    public bool NoFiles => Files.Count == 0;

    /// <summary>Asks the view to put the project name into editing.</summary>
    public event EventHandler? RenameRequested;

    partial void OnHasProjectChanged(bool value) => OnPropertyChanged(nameof(HasNoProject));

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
        if (e.Kind != ProjectChangeKind.Layout)
        {
            Rebuild();
        }
    }

    [RelayCommand]
    private Task NewProjectAsync() => _opener.PickAndAddProjectAsync();

    [RelayCommand]
    private void NewTerminal() => _opener.OpenTerminal();

    [RelayCommand]
    private Task NewFileAsync() => _opener.PickAndOpenFileAsync();

    private void Rebuild()
    {
        var project = _projects.Current;
        HasProject = project is not null;
        _updatingName = true;
        ProjectName = project?.Name ?? "";
        _updatingName = false;
        ProjectPath = project?.Path ?? "";
        Agents.Clear();
        Terminals.Clear();
        Files.Clear();
        if (project is not null)
        {
            var filter = Filter.Trim();
            foreach (var item in project.Items)
            {
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
                (item.Kind switch
                {
                    ProjectItemKind.Agent => Agents,
                    ProjectItemKind.Terminal => Terminals,
                    _ => Files,
                }).Add(row);
            }
        }
        OnPropertyChanged(nameof(NoAgents));
        OnPropertyChanged(nameof(NoTerminals));
        OnPropertyChanged(nameof(NoFiles));
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
