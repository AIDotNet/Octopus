using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Common;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Hosts;

/// <summary>One computer: its projects as groups, each with its terminals and agents, and the sheet that starts a new one.</summary>
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
        foreach (var section in Sections) section.Dispose();
    }

    private void OnProjectsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        foreach (var section in Sections) section.Dispose();
        Sections.Clear();
        foreach (var project in Host.Projects) Sections.Add(new ProjectSectionViewModel(project, _navigator.ShowSession));
        IsEmpty = Sections.Count == 0;
    }
}

/// <summary>A project's group: its name and folder as the header, its sessions as the rows.</summary>
public sealed class ProjectSectionViewModel : IDisposable
{
    private readonly Action<RemoteSession> _open;

    public ProjectSectionViewModel(RemoteProject project, Action<RemoteSession> open)
    {
        Project = project;
        _open = open;
        project.Sessions.CollectionChanged += OnSessionsChanged;
        foreach (var session in project.Sessions) Sessions.Add(new SessionRowViewModel(session, open));
    }

    public RemoteProject Project { get; }
    public string Header => Project.Name + " · " + Project.Path;
    public ObservableCollection<SessionRowViewModel> Sessions { get; } = [];

    public void Dispose()
    {
        Project.Sessions.CollectionChanged -= OnSessionsChanged;
        foreach (var row in Sessions) row.Dispose();
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var row in Sessions) row.Dispose();
        Sessions.Clear();
        foreach (var session in Project.Sessions) Sessions.Add(new SessionRowViewModel(session, _open));
    }
}

/// <summary>The "New" sheet: which project, what to run, and for an agent its model, effort and first prompt.</summary>
public sealed partial class NewSessionViewModel : ObservableObject
{
    private readonly RemoteHost _host;
    private readonly IRemoteWorkspace _workspace;
    private readonly Action<RemoteSession> _created;

    public NewSessionViewModel(RemoteHost host, IRemoteWorkspace workspace, Action<RemoteSession> created)
    {
        _host = host;
        _workspace = workspace;
        _created = created;
    }

    [ObservableProperty] public partial bool IsOpen { get; set; }
    public ObservableCollection<ProjectChoiceViewModel> Projects { get; } = [];
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

    /// <summary>Opens the sheet on the first project and the host's first agent, with nothing typed.</summary>
    public void Open(RemoteProject? project = null)
    {
        Projects.Clear();
        foreach (var candidate in _host.Projects) Projects.Add(new ProjectChoiceViewModel(candidate, Select));
        Select(project ?? _host.Projects.FirstOrDefault());
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

    [RelayCommand]
    private void OpenModelMenu() => IsModelMenuOpen = true;

    [RelayCommand]
    private void SelectModel(string? model)
    {
        SelectedModel = model;
        IsModelMenuOpen = false;
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (Project is not { } project || IsBusy) return;
        IsBusy = true;
        ErrorText = null;
        try
        {
            var agent = Agent;
            var request = new NewSessionRequest(
                agent?.Id,
                agent is null ? null : SelectedModel,
                agent is not null && SelectedEffort >= 0 && SelectedEffort < Efforts.Count ? Efforts[SelectedEffort] : null,
                agent is null || string.IsNullOrWhiteSpace(Prompt) ? null : Prompt);
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

    partial void OnSelectedKindChanged(int value) => ReadAgent();

    partial void OnSelectedModelChanged(string? value) => OnPropertyChanged(nameof(ModelText));

    private void Select(RemoteProject? project)
    {
        foreach (var choice in Projects) choice.IsSelected = ReferenceEquals(choice.Project, project);
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
