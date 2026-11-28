namespace Octopus.Core.Projects;

/// <summary>A local directory the user works in: the container of terminals, agents and files.</summary>
public sealed class Project
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Absolute path of the project directory; the default working directory of its items.</summary>
    public string Path { get; set; } = "";
    /// <summary>
    /// The SSH host (an alias from ~/.ssh/config) when the project is on another machine: <see cref="Path"/> is then a
    /// folder on that host, kept as typed. Null: a folder on this computer.
    /// </summary>
    public string? SshHost { get; set; }
    /// <summary>
    /// The device id of another computer that runs Octopus, when the project lives there (design/12-远程操作.md):
    /// <see cref="Path"/> is then a folder on that computer and <see cref="RemoteId"/> the project's id there. Its
    /// terminals and agents run on that computer; the items kept here are a copy of that computer's list. Null: not such a project.
    /// </summary>
    public string? RemoteHost { get; set; }
    /// <summary>The id the project has on <see cref="RemoteHost"/>.</summary>
    public string? RemoteId { get; set; }
    public string? Color { get; set; }
    /// <summary>A Lucide icon name shown on the project's square instead of its initial; null shows the initial.</summary>
    public string? Icon { get; set; }
    /// <summary>
    /// The file name of the project's picture: a square thumbnail Octopus made from the chosen image and keeps under
    /// <c>&lt;DataDirectory&gt;/project-icons</c>. Shown instead of <see cref="Icon"/> and the color; null: none.
    /// </summary>
    public string? Image { get; set; }
    public bool IsExpanded { get; set; } = true;
    /// <summary>The <see cref="ProjectGroup"/> the project's square sits in on the rail; null: no group.</summary>
    public string? GroupId { get; set; }
    /// <summary>The item that last had focus.</summary>
    public string? ActiveItemId { get; set; }
    /// <summary>The workspace's dock layout (tab groups and splits) as saved by the dock control; null is the default layout.</summary>
    public string? DockLayout { get; set; }
    public List<ProjectItem> Items { get; set; } = [];
    /// <summary>Git worktree workspaces created under ~/.octopus/workspaces/&lt;project&gt;/&lt;branch&gt;; empty when operating only on the project root.</summary>
    public List<ProjectWorkspace> Workspaces { get; set; } = [];
    /// <summary>The port forwards of a project on an SSH host (design/11-SSH隧道.md); empty for any other project.</summary>
    public List<TunnelSpec> Tunnels { get; set; } = [];
    /// <summary>Project-level default account id per agent id (design/13-多账户切换.md §4.3); empty follows global profile defaults.</summary>
    public Dictionary<string, string> AgentAccounts { get; set; } = [];
    /// <summary>Project-level default backend ref (<c>account:*</c>, <c>provider:*</c>, or <c>tunnel:*</c>) per agent id (design/20-模型提供商与模型托管.md §3.2).</summary>
    public Dictionary<string, string> AgentBackends { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ProjectItemKind
{
    Terminal,
    Agent,
    File,
    /// <summary>A tab whose content a plugin renders (design/plugins/06-标签与渲染.md §3).</summary>
    Plugin
}

/// <summary>Something opened under a project. Shown both as a sidebar row and as a tab.</summary>
public sealed class ProjectItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ProjectItemKind Kind { get; set; }
    /// <summary>The default title: shell name, agent name or file name.</summary>
    public string Title { get; set; } = "";
    /// <summary>The title the user typed; shown instead of <see cref="Title"/> when set.</summary>
    public string? CustomTitle { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>
    /// Terminal and agent: its tab was closed, but the item was not. It keeps its sidebar row and keeps running; only
    /// its tab (and the controls drawing it) is gone. Activating the item opens the tab again.
    /// </summary>
    public bool TabClosed { get; set; }

    /// <summary>Terminal and agent: the working directory; null means the project directory.</summary>
    public string? WorkingDirectory { get; set; }
    /// <summary>The <see cref="ProjectWorkspace.Id"/> this item belongs to; null means the project's default directory.</summary>
    public string? WorkspaceId { get; set; }

    /// <summary>Agent: the id of the agent definition.</summary>
    public string? AgentId { get; set; }
    /// <summary>Agent: what was chosen when it was created.</summary>
    public Agents.AgentLaunchOptions? Launch { get; set; }
    /// <summary>Agent: whether this tab runs as an ACP GUI or as a native terminal; null defaults to Terminal for legacy items.</summary>
    public Agents.AgentSurface? Surface { get; set; }
    /// <summary>Agent (GUI): the ACP session id reported by <c>session/new</c> so the session can be resumed or continued in a terminal.</summary>
    public string? AcpSessionId { get; set; }
    /// <summary>
    /// Collaboration: id of the parent agent <see cref="ProjectItem"/> that directly spawned this agent session.
    /// When set, this item is displayed nested underneath its parent in the sidebar and in the parent's collab ribbon.
    /// </summary>
    public string? ParentItemId { get; set; }
    /// <summary>Collaboration: effective isolation mode used when this item was spawned (<c>shared</c>, <c>worktree</c>, or <c>read_only</c>).</summary>
    public string? CollabIsolation { get; set; }
    /// <summary>
    /// Collaboration across computers: the name of the device whose agent delegated this session here (for example
    /// <c>MacBook-Pro</c>); null for a session started on this computer.
    /// </summary>
    public string? CollabOriginDevice { get; set; }
    /// <summary>Collaboration across computers: the delegating session as that device labels it (<c>Claude Code · Lead</c>).</summary>
    public string? CollabOriginSession { get; set; }

    /// <summary>File: absolute path. For a file on an SSH host, the read-only copy kept on this computer.</summary>
    public string? FilePath { get; set; }
    /// <summary>File on an SSH host: the path of the file there. Null for a file of this computer.</summary>
    public string? RemotePath { get; set; }

    /// <summary>Plugin: the view contribution's id. File: the chosen way to open it; null opens it with the default.</summary>
    public string? ViewId { get; set; }
    /// <summary>Plugin and file opened by a plugin: what the plugin saved to come back to (JSON, at most 64 KB).</summary>
    public string? ViewState { get; set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(CustomTitle) ? Title : CustomTitle!;
}

/// <summary>
/// Projects kept together on the rail: closed, one folder square showing its first projects; open, its projects in a
/// tinted capsule. The order of its projects is their order in <see cref="ProjectsDocument.Projects"/>.
/// </summary>
public sealed class ProjectGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>The tint of the folder and the capsule (data, like a project's color); null: a neutral one.</summary>
    public string? Color { get; set; }
    /// <summary>
    /// When true, launching an agent that supports multiple working directories in a local project of this group
    /// automatically passes the group's other local project directories as additional working directories.
    /// </summary>
    public bool MergeWorkspaces { get; set; }
}

/// <summary>The content of projects.json.</summary>
public sealed class ProjectsDocument
{
    public int Version { get; set; } = 1;
    public string? ActiveProjectId { get; set; }
    public List<Project> Projects { get; set; } = [];
    /// <summary>The groups, in their order on the rail (after the projects in no group).</summary>
    public List<ProjectGroup> Groups { get; set; } = [];
    /// <summary>The one group that is open on the rail; opening another closes it. Null: all closed.</summary>
    public string? ExpandedGroupId { get; set; }
    /// <summary>The device currently shown on the sidebar rail (<see cref="DeviceKeys"/>); null follows the active project or local.</summary>
    public string? ActiveDeviceKey { get; set; }
    /// <summary>Last active project id per device key, restored when switching back to that device.</summary>
    public Dictionary<string, string> LastProjectByDevice { get; set; } = [];
    /// <summary>Custom color, icon or picture thumbnail per device key.</summary>
    public Dictionary<string, DeviceAppearance> DeviceAppearances { get; set; } = [];
}
