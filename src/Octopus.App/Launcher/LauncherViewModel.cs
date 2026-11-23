using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Agents;
using Octopus.Agents.Accounts;
using Octopus.App.Common;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.App.Shell.Keymap;
using Octopus.Core.Agents;
using Octopus.Storage;

namespace Octopus.App.Launcher;

/// <summary>The "+" menu: new terminal, open file, and the installed and enabled agents with their launch panels.</summary>
public sealed partial class LauncherViewModel : ObservableObject
{
    private readonly IAgentScanner _scanner;
    private readonly IAgentCatalog _catalog;
    private readonly ISettingsStore _settings;
    private readonly IProjectService _projects;
    private readonly IAgentCommandCompiler _compiler;
    private readonly IModelDiscovery _discovery;
    private readonly IItemOpener _opener;
    private readonly ISettingsNavigator _navigator;
    private readonly IAgentAccountService? _accounts;
    private readonly Dictionary<string, AgentLaunchViewModel> _panels = [];
    private readonly Plugins.PluginWorkbench? _plugins;

    public LauncherViewModel(
        IAgentScanner scanner,
        IAgentCatalog catalog,
        ISettingsStore settings,
        IAgentCommandCompiler compiler,
        IModelDiscovery discovery,
        IItemOpener opener,
        ISettingsNavigator navigator,
        IAppKeymap keymap,
        IProjectService projects,
        Plugins.PluginWorkbench? plugins = null,
        IAgentAccountService? accounts = null)
    {
        _plugins = plugins;
        _accounts = accounts;
        _scanner = scanner;
        _catalog = catalog;
        _settings = settings;
        _compiler = compiler;
        _discovery = discovery;
        _opener = opener;
        _navigator = navigator;
        _projects = projects;
        _projects.Changed += (_, e) =>
        {
            if (e.Kind is ProjectChangeKind.CurrentProject or ProjectChangeKind.ProjectAccounts) UiContext.PostToUi(Refresh);
        };
        NewTerminalHint = keymap.Hint(AppCommand.NewTerminal);
        _scanner.Changed += (_, _) => UiContext.PostToUi(Refresh);
        _catalog.Changed += (_, _) => UiContext.PostToUi(Refresh);
        _settings.Changed += (_, _) => UiContext.PostToUi(Refresh);
        if (_accounts is not null) _accounts.Changed += (_, _) => UiContext.PostToUi(Refresh);
        if (plugins is not null) plugins.Manager.HostChanged += (_, _) => UiContext.PostToUi(RefreshViews);
        Refresh();
    }

    /// <summary>Plugin views offered in the menu, under the agents.</summary>
    public ObservableCollection<LauncherViewEntry> Views { get; } = [];
    public bool HasViews => Views.Count > 0;

    private void RefreshViews()
    {
        Views.Clear();
        if (_plugins is null) return;
        foreach (var (host, view) in _plugins.LauncherViews)
        {
            var icon = Plugins.Rendering.UiIcons.Parse(view.Icon) ?? TKCode.Arc.Icons.Lucide.LucideIconKind.LayoutGrid;
            Views.Add(new LauncherViewEntry(view.Id, Plugins.PluginWorkbench.LocalizedTitle(view), Plugins.PluginWorkbench.NameOf(host), icon, entry =>
            {
                Close();
                if (_plugins.FindView(entry.Id) is var (owner, contribution)) _plugins.OpenView(owner, contribution);
            }));
        }
        OnPropertyChanged(nameof(HasViews));
    }

    public string NewTerminalHint { get; }
    public ObservableCollection<LauncherAgentViewModel> Agents { get; } = [];

    [ObservableProperty] public partial bool IsScanning { get; private set; }
    [ObservableProperty] public partial LauncherAgentViewModel? ExpandedAgent { get; private set; }
    [ObservableProperty] public partial AgentLaunchViewModel? Panel { get; private set; }

    /// <summary>In a project on an SSH host no agent is offered: agents need this computer's files and programs.</summary>
    /// <summary>The current project is somewhere agents cannot run (an SSH host): the menu says so instead of listing them.</summary>
    public bool IsRemoteProject => _projects.Current is { } current && !Octopus.Core.Projects.ProjectLocation.CanRunAgents(current);
    public bool ShowRemoteNote => IsRemoteProject;
    public bool ShowScanning => IsScanning && Agents.Count == 0 && !IsRemoteProject;
    public bool ShowNoAgents => !IsScanning && Agents.Count == 0 && !IsRemoteProject;

    /// <summary>Raised when the menu should close (something was launched).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Reads the installed agents again and collapses the second panel.</summary>
    public void Refresh()
    {
        var agents = _settings.Current.Agents;
        var rows = new List<LauncherAgentViewModel>();
        var installed = IsRemoteProject ? Enumerable.Empty<AgentInstallation>() : _scanner.Current.Installations;
        if (_projects.Current is { RemoteHost: { } remoteHost })
        {
            // A project on another computer: the agents that computer has, with its own default first. The panel beside
            // a row is built from this computer's definition of the same agent, when it knows one.
            installed = [];
            foreach (var agent in Composition.AppHost.Get<Remote.RemoteClientRuntime>().AgentsOf(remoteHost))
            {
                rows.Add(new LauncherAgentViewModel(agent.Id, agent.Name, agent.IsDefault, LaunchAgent, Expand));
            }
        }
        foreach (var installation in installed)
        {
            var definition = _catalog.Find(installation.AgentId);
            if (definition is null || !agents.ProfileOf(definition.Id).Enabled || rows.Any(r => r.Id == definition.Id))
            {
                continue;
            }
            rows.Add(new LauncherAgentViewModel(definition.Id, definition.Name, definition.Id == agents.DefaultAgentId, LaunchAgent, Expand));
        }
        // The default agent first; the rest keep the catalog's order.
        var ordered = rows.OrderByDescending(r => r.IsDefault).ToList();
        var expandedId = ExpandedAgent?.Id;
        Agents.Clear();
        foreach (var row in ordered)
        {
            Agents.Add(row);
        }
        IsScanning = _scanner.IsScanning;
        RefreshViews();
        ClearPanels();
        Expand(Agents.FirstOrDefault(a => a.Id == expandedId));
        OnPropertyChanged(nameof(ShowScanning));
        OnPropertyChanged(nameof(ShowNoAgents));
        OnPropertyChanged(nameof(IsRemoteProject));
        OnPropertyChanged(nameof(ShowRemoteNote));
    }

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowScanning));
        OnPropertyChanged(nameof(ShowNoAgents));
    }

    /// <summary>Shows the second panel of an agent; null collapses it.</summary>
    public void Expand(LauncherAgentViewModel? agent)
    {
        if (ReferenceEquals(agent, ExpandedAgent) && (agent is null || Panel is not null))
        {
            return;
        }
        if (ExpandedAgent is not null)
        {
            ExpandedAgent.IsExpanded = false;
        }
        ExpandedAgent = agent;
        Panel = agent is null ? null : PanelOf(agent);
        if (agent is not null)
        {
            agent.IsExpanded = true;
        }
    }

    [RelayCommand]
    private void NewTerminal()
    {
        _opener.OpenTerminal();
        Close();
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        Close();
        await _opener.PickAndOpenFileAsync();
    }

    /// <summary>Clicking the agent row itself: start it with its default configuration.</summary>
    private void LaunchAgent(LauncherAgentViewModel? agent)
    {
        if (agent is null)
        {
            return;
        }
        if (_projects.Current is { RemoteHost: not null })
        {
            // The host's own settings for that agent apply: nothing of this computer's is sent along.
            _opener.OpenAgent(agent.Id, null);
            Close();
            return;
        }
        var profile = _settings.Current.Agents.ProfileOf(agent.Id);
        _opener.OpenAgent(agent.Id, new AgentLaunchOptions { Model = profile.Model, Effort = profile.Effort, Fast = profile.Fast });
        Close();
    }

    [RelayCommand]
    private void OpenAgentSettings()
    {
        Close();
        _navigator.OpenSettings("agents");
    }

    private AgentLaunchViewModel? PanelOf(LauncherAgentViewModel agent)
    {
        if (_panels.TryGetValue(agent.Id, out var existing))
        {
            return existing;
        }
        var definition = _catalog.Find(agent.Id);
        if (definition is null)
        {
            return null;
        }
        var current = _settings.Current.Agents;
        var profile = current.ProfileOf(agent.Id);
        IReadOnlyList<AgentAccountRecord>? accounts = null;
        string? preferredAccountId = null;
        if (_accounts is not null && _projects.Current is { RemoteHost: null } localProject)
        {
            accounts = _accounts.GetAccounts(definition);
            preferredAccountId = _accounts.ResolvePreferredAccountId(definition, profile, localProject);
        }
        var panel = new AgentLaunchViewModel(
            definition,
            profile,
            current.DefaultPermission,
            _compiler,
            _discovery,
            _scanner.Find(agent.Id)?.Path,
            options =>
            {
                _opener.OpenAgent(agent.Id, options);
                Close();
            },
            accounts,
            preferredAccountId);
        _panels[agent.Id] = panel;
        return panel;
    }

    private void ClearPanels()
    {
        foreach (var panel in _panels.Values)
        {
            panel.Dispose();
        }
        _panels.Clear();
    }

    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
