using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Common;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Sessions;

/// <summary>
/// Everything about one session and what can be done to it (design/ui/移动端功能扩展方案.html, M-C ②). It is also where a
/// session that runs as a GUI conversation on the computer opens, since the phone has no terminal to show for it (G-0).
/// </summary>
public sealed partial class SessionDetailViewModel : ObservableObject, IDisposable
{
    private readonly INavigator _navigator;

    public SessionDetailViewModel(RemoteSession session, IRemoteWorkspace workspace, INavigator navigator)
    {
        Session = session;
        _navigator = navigator;
        // Closed from here: nothing left to show.
        Actions = new SessionActionsViewModel(workspace, navigator, session, _ => navigator.Back());
        var agent = session.AgentId is null ? null : session.Project.Host.Agents.FirstOrDefault(a => a.Id == session.AgentId);
        AgentName = agent?.Name ?? session.AgentId;
        Initial = agent?.Initial ?? (IsAgent ? "A" : "");
        Tint = agent is not null && Color.TryParse(agent.Color, out var color) ? new SolidColorBrush(color) : null;
        session.PropertyChanged += OnSessionChanged;
    }

    public RemoteSession Session { get; }
    public SessionActionsViewModel Actions { get; }

    public string Title => Session.Title;
    public bool IsAgent => Session.Kind == SessionKind.Agent;
    public bool IsTerminal => !IsAgent;
    public bool IsGui => Session.IsGui;
    public bool CanOpenTerminal => !Session.IsGui;
    public string Initial { get; }
    public IBrush? Tint { get; }

    public string HostText => Session.Project.Host.Name;
    public string ProjectText => Session.Project.Name + " · " + Session.Project.Path;
    public string? AgentName { get; }
    public string KindText => IsAgent ? AgentName ?? "" : MobileLangResources.Get(MobileLangResourceKind.Terminal);
    public string ModelText => string.Join(" · ", new[] { Session.Model, Session.Effort }.Where(part => !string.IsNullOrEmpty(part))) is { Length: > 0 } text
        ? text
        : MobileLangResources.Get(MobileLangResourceKind.NewSessionModelDefault);
    public string SurfaceText => MobileLangResources.Get(Session.IsGui ? MobileLangResourceKind.SurfaceGui : MobileLangResourceKind.Terminal);
    public string WorkspaceText => Session.WorkspaceBranch is { Length: > 0 } branch
        ? Session.WorkspaceTaskKey is { Length: > 0 } task ? $"{branch} · {task}" : branch
        : MobileLangResources.Get(MobileLangResourceKind.WorkspaceDefaultTitle);
    public string StatusText => Session.Activity == SessionActivity.Idle
        ? MobileLangResources.Get(MobileLangResourceKind.Idle)
        : SessionRowViewModel.StatusOf(Session.Activity);
    public string? Preview => Session.Preview;
    public bool HasPreview => !string.IsNullOrEmpty(Session.Preview);

    [RelayCommand]
    private void Back() => _navigator.Back();

    [RelayCommand]
    private void OpenTerminal()
    {
        if (!Session.IsGui) _navigator.ShowSession(Session);
    }

    public void Dispose() => Session.PropertyChanged -= OnSessionChanged;

    // Everything shown comes from the session: one change refreshes the page.
    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(string.Empty);
}
