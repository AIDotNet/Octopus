namespace Octopus.Core.Projects;

/// <summary>Where a project's folder is.</summary>
public enum ProjectLocationKind
{
    /// <summary>A folder on this computer.</summary>
    Local,
    /// <summary>A folder on an SSH host: its terminals are ssh sessions, its files are read through ssh.</summary>
    Ssh,
    /// <summary>A folder on another computer that runs Octopus: that computer runs the project's terminals and agents (design/12-远程操作.md).</summary>
    Remote,
}

/// <summary>
/// What follows from where a project is, asked in one place: code that only needs to know "can an agent run here" or
/// "how is this place written" does not look at <see cref="Project.SshHost"/> itself, so another kind of place changes
/// this file and not every caller. Code that really is about ssh (tunnels, reading folders through ssh) still does.
/// </summary>
public static class ProjectLocation
{
    public static ProjectLocationKind KindOf(Project project) =>
        project.RemoteHost is not null ? ProjectLocationKind.Remote
        : project.SshHost is not null ? ProjectLocationKind.Ssh
        : ProjectLocationKind.Local;

    /// <summary>True for a folder of this computer: its files can be opened, picked and shown in the file manager directly.</summary>
    public static bool IsOnThisComputer(Project project) => KindOf(project) == ProjectLocationKind.Local;

    /// <summary>True for a project that lives on another computer running Octopus.</summary>
    public static bool IsOnRemoteHost(Project project) => KindOf(project) == ProjectLocationKind.Remote;

    /// <summary>
    /// Agents need Octopus's own configuration, hooks and the agent programs where the project is. An SSH host has none
    /// of that; this computer has, and so has another computer that runs Octopus.
    /// </summary>
    public static bool CanRunAgents(Project project) => KindOf(project) != ProjectLocationKind.Ssh;

    /// <summary>
    /// The place as people write it: the folder, with the host before it when it is not on this computer.
    /// <paramref name="remoteHostName"/> is the name of the project's Octopus host when the caller knows it.
    /// </summary>
    public static string Describe(Project project, string? remoteHostName = null) =>
        project.SshHost is { } host ? host + ":" + project.Path
        : project.RemoteHost is { } remote ? (remoteHostName ?? remote) + ":" + project.Path
        : project.Path;
}
