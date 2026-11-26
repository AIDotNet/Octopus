using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Common;

/// <summary>One session in a list: its square, title, what it runs, what it is doing, and its state.</summary>
public sealed partial class SessionRowViewModel : ObservableObject, IDisposable
{
    private readonly Action<RemoteSession> _open;
    private DateTime? _heldAt;

    public SessionRowViewModel(RemoteSession session, Action<RemoteSession> open)
    {
        Session = session;
        _open = open;
        var agent = session.AgentId is null ? null : session.Project.Host.Agents.FirstOrDefault(a => a.Id == session.AgentId);
        IsAgent = session.Kind == SessionKind.Agent;
        Initial = agent?.Initial ?? (IsAgent ? "A" : "");
        Tint = agent is not null && Color.TryParse(agent.Color, out var color) ? new SolidColorBrush(color) : null;
        session.PropertyChanged += OnSessionChanged;
    }

    public RemoteSession Session { get; }
    public bool IsAgent { get; }
    public bool IsTerminal => !IsAgent;
    /// <summary>The letter in an agent's square.</summary>
    public string Initial { get; }
    /// <summary>The agent's color; null for a terminal or an agent the host did not describe.</summary>
    public IBrush? Tint { get; }
    public string Title => Session.Title;
    public string? Detail => Session.Detail;
    public string? Preview => Session.Preview;
    public bool HasPreview => !string.IsNullOrEmpty(Session.Preview);
    public string? WorkspaceBranch => Session.WorkspaceBranch;
    public bool HasWorkspace => !string.IsNullOrEmpty(Session.WorkspaceBranch);
    public string? WorkspaceTaskKey => Session.WorkspaceTaskKey;
    public bool HasWorkspaceTask => !string.IsNullOrEmpty(Session.WorkspaceTaskKey);
    /// <summary>Compact worktree pill shown on a session row when all workspaces of a project are listed together.</summary>
    public string? WorkspaceBadgeText => HasWorkspaceTask ? WorkspaceTaskKey : WorkspaceBranch;
    public bool IsWaiting => Session.Activity == SessionActivity.Waiting;
    public bool IsWorking => Session.Activity == SessionActivity.Working;
    public bool IsError => Session.Activity == SessionActivity.Error;
    public bool HasStatus => Session.Activity != SessionActivity.Idle;
    public string StatusText => StatusOf(Session.Activity);

    public static string StatusOf(SessionActivity activity) => activity switch
    {
        SessionActivity.Waiting => MobileLangResources.Get(MobileLangResourceKind.StatusWaiting),
        SessionActivity.Working => MobileLangResources.Get(MobileLangResourceKind.StatusWorking),
        SessionActivity.Error => MobileLangResources.Get(MobileLangResourceKind.StatusError),
        _ => "",
    };

    /// <summary>Runs as a GUI conversation on the host: tagged, and opens its details.</summary>
    public bool IsGui => Session.IsGui;

    /// <summary>Where its work came from when an agent delegated it ("来自 MacBook · Claude Code · Lead"); null when a person started it.</summary>
    public string? OriginText => OriginOf(Session);
    public bool HasOrigin => OriginText is not null;

    public static string? OriginOf(RemoteSession session)
    {
        // The host reports a parent only within itself, and another computer as the origin device.
        (string Title, bool SameProject, string? Host)? parent =
            session.ParentItemId is { } parentId && session.Project.Sessions.FirstOrDefault(s => s.Id == parentId) is { } p
                ? (p.Title, true, null)
                : null;
        var origin = Octopus.Core.Agents.CollabOrigin.Describe(parent, session.OriginDevice, session.OriginSession);
        return origin.Kind switch
        {
            Octopus.Core.Agents.CollabOriginKind.SameProjectParent or Octopus.Core.Agents.CollabOriginKind.LocalParentElsewhere =>
                MobileLangResources.Format(MobileLangResourceKind.SessionDelegatedBy, origin.Session ?? ""),
            Octopus.Core.Agents.CollabOriginKind.OtherDevice when origin.Session is { } by =>
                MobileLangResources.Format(MobileLangResourceKind.SessionOriginFromDeviceBy, origin.Device ?? "", by),
            Octopus.Core.Agents.CollabOriginKind.OtherDevice =>
                MobileLangResources.Format(MobileLangResourceKind.SessionOriginFromDevice, origin.Device ?? ""),
            _ => null,
        };
    }

    /// <summary>The row was held and its menu opened: the tap that ends the press does not open the session too.</summary>
    public void Held() => _heldAt = DateTime.UtcNow;

    [RelayCommand]
    private void Open()
    {
        // The release of a long press arrives as a tap; one long after is a new tap.
        if (_heldAt is { } held && DateTime.UtcNow - held < TimeSpan.FromSeconds(2))
        {
            _heldAt = null;
            return;
        }
        _open(Session);
    }

    public void Dispose() => Session.PropertyChanged -= OnSessionChanged;

    // Everything shown comes from the session: one change refreshes the whole row.
    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(string.Empty);
}
