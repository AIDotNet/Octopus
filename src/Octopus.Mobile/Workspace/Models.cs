using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
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
/// <param name="SupportsAcp">True when this agent can be launched as an ACP GUI conversation on the host.</param>
/// <param name="DefaultGui">True when the host defaults this agent to GUI mode.</param>
public sealed record AgentOption(
    string Id,
    string Name,
    string Initial,
    string Color,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> Efforts,
    bool SupportsAcp = false,
    bool DefaultGui = false);

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
    /// <summary>
    /// A computer of the account this phone is not paired with yet that, online, lets devices of the account in with
    /// its access password (its settings, "Account and devices"). False while it is offline, when the owner turned
    /// password access off, and for an older version that has none: then only pairing gets in.
    /// </summary>
    [ObservableProperty] public partial bool AcceptsPassword { get; set; }
    /// <summary>The system it runs ("macOS", "Windows"…), when the account's list says.</summary>
    public string? Platform { get; set; }
    public ObservableCollection<RemoteProject> Projects { get; } = [];
    public ObservableCollection<RemoteBot> Bots { get; } = [];
    public ObservableCollection<RemoteBotGroup> BotGroups { get; } = [];
    public IReadOnlyList<AgentOption> Agents { get; set; } = [];

    public bool IsOnline => Link != HostLink.Offline;

    partial void OnLinkChanged(HostLink value) => OnPropertyChanged(nameof(IsOnline));
}

/// <summary>
/// A device of the account that is not a computer to operate: a phone, this one included. Phones are never hosts; the
/// host list only names them.
/// </summary>
/// <param name="IsCurrent">This phone itself.</param>
/// <param name="Platform">The system it runs ("iOS", "Android"…), when the account's list says.</param>
public sealed record AccountDevice(string Id, string Name, bool IsCurrent, bool IsOnline, DateTimeOffset? LastSeen, string? Platform = null);

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
public sealed partial class RemoteProject(RemoteHost host, string id, string name, string path) : ObservableObject
{
    public RemoteHost Host { get; } = host;
    public string Id { get; } = id;
    public string Name { get; } = name;
    /// <summary>The folder on the host, as the host shows it.</summary>
    public string Path { get; } = path;
    /// <summary>Project accent color as <c>#RRGGBB</c>, synced from the desktop rail when set.</summary>
    [ObservableProperty] public partial string? Color { get; set; }
    /// <summary>Optional Lucide or pixel icon name (<c>pixel:&lt;id&gt;</c>) synced from the desktop rail.</summary>
    [ObservableProperty] public partial string? Icon { get; set; }
    /// <summary>Versioned file name of the project's custom picture thumbnail on the host; null when using an icon or initial.</summary>
    [ObservableProperty] public partial string? ImageKey { get; set; }
    /// <summary>Decoded custom picture thumbnail cached locally for <see cref="ImageKey"/>; null when none or not yet loaded.</summary>
    [ObservableProperty] public partial Bitmap? ImageBitmap { get; set; }
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
    /// <summary>The model it was started with, as the host labels it; null for a terminal or the agent's default.</summary>
    public string? Model { get; init; }
    /// <summary>The effort it was started with, as the host labels it; null when none was chosen.</summary>
    public string? Effort { get; init; }
    /// <summary>
    /// An agent that runs as an ACP conversation in a GUI tab on the host: there is no terminal to show, so the phone
    /// opens its details instead (design/ui/移动端功能扩展方案.html, G-0).
    /// </summary>
    public bool IsGui { get; init; }
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
    /// <summary>Multi-agent collaboration: the session of the same host whose agent started this one; null when a person did.</summary>
    [ObservableProperty] public partial string? ParentItemId { get; set; }
    /// <summary>Set when another computer's agent delegated this session here: that device's name.</summary>
    [ObservableProperty] public partial string? OriginDevice { get; set; }
    /// <summary>The delegating session on <see cref="OriginDevice"/> (<c>Claude Code · Lead</c>).</summary>
    [ObservableProperty] public partial string? OriginSession { get; set; }
}

/// <summary>What to start in a project.</summary>
/// <param name="AgentId">Null for a plain terminal.</param>
/// <param name="Model">Null for the agent's default.</param>
/// <param name="WorkspaceId">Null for the project's default folder; otherwise the id of a branch workspace.</param>
/// <param name="InjectTaskPrompt">When true and the target workspace has a linked task, prepend task context to the initial prompt.</param>
/// <param name="LaunchAsGui">When true, request ACP GUI conversation launch; when false, request terminal launch; null lets the host decide.</param>
public sealed record NewSessionRequest(
    string? AgentId,
    string? Model,
    string? Effort,
    string? Prompt,
    string? WorkspaceId = null,
    bool InjectTaskPrompt = false,
    bool? LaunchAsGui = null);

/// <summary>One entry while browsing the host's folders for a new project.</summary>
/// <param name="Kind"><c>folder</c>, <c>drive</c> or <c>home</c>.</param>
/// <param name="IsProject">The folder is already one of the host's projects.</param>
public sealed record RemoteFolderEntry(string Name, string Path, string Kind, bool IsProject);

/// <summary>One folder of the host as the folder picker shows it.</summary>
/// <param name="Path">The folder; empty for the list of drives (Windows).</param>
/// <param name="Parent">The folder above; empty for the list of drives, null at the top.</param>
/// <param name="Roots">Home and the drives or <c>/</c>: the shortcuts above the list.</param>
/// <param name="Folders">The folders in it.</param>
public sealed record RemoteFolderListing(string Path, string? Parent, string Separator, string Home, IReadOnlyList<RemoteFolderEntry> Roots, IReadOnlyList<RemoteFolderEntry> Folders);
