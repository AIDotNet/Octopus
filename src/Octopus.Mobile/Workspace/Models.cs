using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Octopus.Mobile.Workspace;

/// <summary>How this phone reaches a host right now.</summary>
public enum HostLink
{
    Offline,
    /// <summary>Directly, on the same local network.</summary>
    Lan,
    /// <summary>Through the relay service.</summary>
    Relay,
}

public enum SessionKind
{
    Terminal,
    Agent,
}

/// <summary>What the program of a session is doing, as the host reports it (the host's <c>ItemActivity</c>).</summary>
public enum SessionActivity
{
    Idle,
    Working,
    /// <summary>The agent stopped to ask the user.</summary>
    Waiting,
    Error,
}

/// <summary>An agent installed and enabled on a host, with what can be chosen when starting it.</summary>
/// <param name="Initial">One letter for its square.</param>
/// <param name="Color">The color of its square, as <c>#RRGGBB</c>.</param>
public sealed record AgentOption(string Id, string Name, string Initial, string Color, IReadOnlyList<string> Models, IReadOnlyList<string> Efforts);

/// <summary>
/// A computer running Octopus that this account can see. Everything under it (projects, sessions) lives on that
/// computer; the phone only shows it. Changes are raised on the UI thread.
/// </summary>
public sealed partial class RemoteHost : ObservableObject
{
    public RemoteHost(string id, string name, HostLink link, bool isPaired)
    {
        Id = id;
        Name = name;
        Link = link;
        IsPaired = isPaired;
    }

    public string Id { get; }
    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial HostLink Link { get; set; }
    /// <summary>False for a computer of the account this phone has not been allowed on yet.</summary>
    [ObservableProperty] public partial bool IsPaired { get; set; }
    /// <summary>When the host was last reachable; shown while it is offline.</summary>
    public DateTimeOffset? LastSeen { get; set; }
    public ObservableCollection<RemoteProject> Projects { get; } = [];
    public IReadOnlyList<AgentOption> Agents { get; set; } = [];

    public bool IsOnline => Link != HostLink.Offline;

    partial void OnLinkChanged(HostLink value) => OnPropertyChanged(nameof(IsOnline));
}

/// <summary>An isolated git worktree workspace of a project on the host.</summary>
public sealed record RemoteBranchWorkspace(
    string Id,
    string Name,
    string Branch,
    string Path,
    string? TaskKind = null,
    string? TaskKey = null,
    string? TaskTitle = null,
    string? TaskUrl = null)
{
    public string DisplayName => !string.IsNullOrWhiteSpace(Branch) ? Branch : Name;
    public bool HasTask => !string.IsNullOrWhiteSpace(TaskKey);
}

public sealed record RemoteBranchOption(string Name, bool IsRemote, bool IsCurrent, string? OccupiedBy)
{
    public bool IsOccupied => !string.IsNullOrEmpty(OccupiedBy);
}

public sealed record RemoteTaskOption(string Kind, string Key, string Title, string? Url, string? Body, string SuggestedBranch, string? HeadBranch);

public sealed record RemoteWorkspaceOptions(
    bool IsGitRepository,
    string? DefaultBranch,
    IReadOnlyList<RemoteBranchOption> Branches,
    bool GhAvailable,
    IReadOnlyList<RemoteTaskOption> Issues,
    IReadOnlyList<RemoteTaskOption> PullRequests,
    string? WorkspacesDirectory = null)
{
    public static RemoteWorkspaceOptions Empty { get; } = new(false, null, [], false, [], []);
}

public sealed record CreateBranchWorkspaceRequest(
    bool CreateBranch,
    string BranchName,
    string? BaseRef,
    bool IsRemoteBranch,
    string? TaskKind = null,
    string? TaskKey = null,
    string? TaskTitle = null,
    string? TaskUrl = null,
    string? TaskBody = null);

/// <summary>A project on a host: a folder there, with its branch workspaces, terminals and agents.</summary>
public sealed class RemoteProject(RemoteHost host, string id, string name, string path)
{
    public RemoteHost Host { get; } = host;
    public string Id { get; } = id;
    public string Name { get; } = name;
    /// <summary>The folder on the host, as the host shows it.</summary>
    public string Path { get; } = path;
    public bool IsGitRepository { get; set; }
    public string? DefaultBranch { get; set; }
    public ObservableCollection<RemoteBranchWorkspace> Workspaces { get; } = [];
    public ObservableCollection<RemoteSession> Sessions { get; } = [];
}

/// <summary>A terminal or agent of a project: the host's item and its running program. Changes are raised on the UI thread.</summary>
public sealed partial class RemoteSession : ObservableObject
{
    public RemoteSession(RemoteProject project, string id, SessionKind kind, string title)
    {
        Project = project;
        Id = id;
        Kind = kind;
        Title = title;
    }

    public RemoteProject Project { get; }
    public string Id { get; }
    public SessionKind Kind { get; }
    /// <summary>The agent's id on the host; null for a terminal.</summary>
    public string? AgentId { get; init; }
    /// <summary>The second line of its row: model and effort for an agent, the shell for a terminal.</summary>
    public string? Detail { get; init; }
    /// <summary>Null when running in the project's default folder; otherwise the branch workspace id.</summary>
    [ObservableProperty] public partial string? WorkspaceId { get; set; }
    /// <summary>Branch name of the workspace this session runs in; null for default folder.</summary>
    [ObservableProperty] public partial string? WorkspaceBranch { get; set; }
    /// <summary>Optional short task badge (e.g. #142) from the session's workspace.</summary>
    [ObservableProperty] public partial string? WorkspaceTaskKey { get; set; }
    [ObservableProperty] public partial string Title { get; set; }
    [ObservableProperty] public partial SessionActivity Activity { get; set; }
    /// <summary>One line about what it is doing or asking; null when there is nothing to say.</summary>
    [ObservableProperty] public partial string? Preview { get; set; }
    /// <summary>The terminal's size on the host.</summary>
    [ObservableProperty] public partial int Columns { get; set; }
    [ObservableProperty] public partial int Rows { get; set; }
    /// <summary>The name of the device whose view decides the size; null when it is this phone.</summary>
    [ObservableProperty] public partial string? SizeOwner { get; set; }
}

/// <summary>What to start in a project.</summary>
/// <param name="AgentId">Null for a plain terminal.</param>
/// <param name="Model">Null for the agent's default.</param>
/// <param name="WorkspaceId">Null for the project's default folder; otherwise the id of a branch workspace.</param>
/// <param name="InjectTaskPrompt">When true and the target workspace has a linked task, prepend task context to the initial prompt.</param>
public sealed record NewSessionRequest(
    string? AgentId,
    string? Model,
    string? Effort,
    string? Prompt,
    string? WorkspaceId = null,
    bool InjectTaskPrompt = false);
