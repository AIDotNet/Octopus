namespace Octopus.Core.Projects;

/// <summary>A local directory the user works in: the container of terminals, agents and files.</summary>
public sealed class Project
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Absolute path of the project directory; the default working directory of its items.</summary>
    public string Path { get; set; } = "";
    public string? Color { get; set; }
    public bool IsExpanded { get; set; } = true;
    /// <summary>The item that last had focus.</summary>
    public string? ActiveItemId { get; set; }
    /// <summary>The workspace's dock layout (tab groups and splits) as saved by the dock control; null is the default layout.</summary>
    public string? DockLayout { get; set; }
    public List<ProjectItem> Items { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ProjectItemKind
{
    Terminal,
    Agent,
    File
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

    /// <summary>File: absolute path.</summary>
    public string? FilePath { get; set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(CustomTitle) ? Title : CustomTitle!;
}

/// <summary>The content of projects.json.</summary>
public sealed class ProjectsDocument
{
    public int Version { get; set; } = 1;
    public string? ActiveProjectId { get; set; }
    public List<Project> Projects { get; set; } = [];
}
