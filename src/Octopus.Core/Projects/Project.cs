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
    /// <summary>The port forwards of a project on an SSH host (design/11-SSH隧道.md); empty for any other project.</summary>
    public List<TunnelSpec> Tunnels { get; set; } = [];
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

    /// <summary>Terminal and agent: the working directory; null means the project directory.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Agent: the id of the agent definition.</summary>
    public string? AgentId { get; set; }
    /// <summary>Agent: what was chosen when it was created.</summary>
    public Agents.AgentLaunchOptions? Launch { get; set; }

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
}
