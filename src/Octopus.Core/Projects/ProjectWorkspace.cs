using System.Security.Cryptography;
using System.Text;

namespace Octopus.Core.Projects;

/// <summary>The external task or note linked to a branch workspace.</summary>
public enum WorkspaceTaskKind
{
    GitHubIssue,
    GitHubPr,
    Url,
    Note
}

/// <summary>Optional task context (GitHub issue, PR, link or note) attached to a branch workspace.</summary>
public sealed class WorkspaceTaskLink
{
    public WorkspaceTaskKind Kind { get; set; }
    /// <summary>Short badge key shown in the sidebar and terminal header (e.g. <c>#142</c>, <c>PR #138</c>).</summary>
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Url { get; set; }
    /// <summary>Brief snapshot of the description, used to seed the first agent prompt when requested.</summary>
    public string? Body { get; set; }
}

/// <summary>
/// An isolated git worktree created for a project under <c>~/.octopus/workspaces/&lt;project&gt;/&lt;branch&gt;</c>.
/// Items with <see cref="ProjectItem.WorkspaceId"/> matching <see cref="Id"/> run in <see cref="Path"/>; items with a
/// null <see cref="ProjectItem.WorkspaceId"/> run in the project's default directory (<see cref="Project.Path"/>).
/// </summary>
public sealed class ProjectWorkspace
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Display label in the sidebar (defaults to <see cref="Branch"/>).</summary>
    public string Name { get; set; } = "";
    /// <summary>Real git branch name (preserves slashes such as <c>feat/worktree-ui</c>).</summary>
    public string Branch { get; set; } = "";
    /// <summary>The ref this branch was branched from when created; null when reusing an existing branch or discovered externally.</summary>
    public string? BaseRef { get; set; }
    /// <summary>Absolute directory path of the worktree on disk.</summary>
    public string Path { get; set; } = "";
    /// <summary>Optional bound issue, PR, link or note.</summary>
    public WorkspaceTaskLink? Task { get; set; }
    /// <summary>Whether this workspace group is folded in the sidebar tree.</summary>
    public bool IsCollapsed { get; set; }
    /// <summary>Pinned workspaces are always protected from automatic cleanup.</summary>
    public bool IsPinned { get; set; }
    /// <summary>Commit hash recorded when the workspace was created; helps detect fast-forward merges.</summary>
    public string? InitialHead { get; set; }
    /// <summary>Last observed activity in this workspace (null falls back to <see cref="CreatedAt"/>).</summary>
    public DateTimeOffset? LastActivityAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string DisplayName => !string.IsNullOrWhiteSpace(Branch) ? Branch : Name;
}

/// <summary>
/// Path conventions for git worktrees kept under <c>~/.octopus/workspaces/&lt;project&gt;/&lt;branch&gt;</c>.
/// Separates filesystem-safe directory names from real git branch names so slashes in branch names never mutate the git ref.
/// </summary>
public static class WorkspacePaths
{
    private const int MaxSegmentLength = 80;

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Default root directory where Octopus places managed branch workspaces: <c>~/.octopus/workspaces</c>.</summary>
    public static string DefaultWorkspacesRoot(string? homeDirectory = null)
    {
        var home = ResolveHome(homeDirectory);
        return System.IO.Path.Combine(home, ".octopus", "workspaces");
    }

    /// <summary>Expands a leading <c>~</c> (<c>~</c>, <c>~/...</c>, or <c>~\...</c>) against <paramref name="homeDirectory"/>.</summary>
    public static string ExpandHome(string path, string? homeDirectory = null)
    {
        var trimmed = path.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '~') return trimmed;
        var home = ResolveHome(homeDirectory);
        if (trimmed.Length == 1) return home;
        if (trimmed[1] is '/' or '\\')
        {
            var rest = trimmed[2..].TrimStart('/', '\\');
            return rest.Length == 0 ? home : System.IO.Path.Combine(home, rest);
        }
        return trimmed;
    }

    /// <summary>
    /// Validates and resolves a custom workspaces root directory (supporting a leading <c>~</c>).
    /// Empty or whitespace resolves to <see cref="DefaultWorkspacesRoot"/> and returns true; relative or malformed paths return false.
    /// </summary>
    public static bool TryResolveRootDirectory(string? rootDirectory, string? homeDirectory, out string resolvedPath)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            resolvedPath = DefaultWorkspacesRoot(homeDirectory);
            return true;
        }
        var expanded = ExpandHome(rootDirectory, homeDirectory);
        if (expanded.Length == 0
            || expanded.Contains('\0')
            || expanded.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0
            || !System.IO.Path.IsPathFullyQualified(expanded))
        {
            resolvedPath = "";
            return false;
        }
        try
        {
            resolvedPath = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(expanded));
            return resolvedPath.Length > 0;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            resolvedPath = "";
            return false;
        }
    }

    /// <summary>
    /// Root directory where Octopus places managed branch workspaces. Uses <paramref name="rootDirectory"/> when valid,
    /// otherwise falls back to <c>~/.octopus/workspaces</c>.
    /// </summary>
    public static string WorkspacesRoot(string? homeDirectory = null, string? rootDirectory = null) =>
        !string.IsNullOrWhiteSpace(rootDirectory) && TryResolveRootDirectory(rootDirectory, homeDirectory, out var custom)
            ? custom
            : DefaultWorkspacesRoot(homeDirectory);

    private static string ResolveHome(string? homeDirectory) =>
        string.IsNullOrWhiteSpace(homeDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : homeDirectory;

    /// <summary>
    /// Converts a project name or git branch name into a single safe directory segment.
    /// Slashes (<c>/</c>, <c>\</c>), whitespace and invalid filename characters become <c>-</c>.
    /// </summary>
    public static string SanitizeSegment(string? value, string fallback = "workspace")
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var sb = new StringBuilder(value.Length);
        var lastDash = false;
        foreach (var ch in value.Trim())
        {
            var invalid = ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsWhiteSpace(ch) || char.IsControl(ch);
            if (invalid)
            {
                if (!lastDash && sb.Length > 0)
                {
                    sb.Append('-');
                    lastDash = true;
                }
            }
            else
            {
                sb.Append(ch);
                lastDash = false;
            }
        }
        var cleaned = sb.ToString().Trim('-', '.');
        if (cleaned.Length == 0) return fallback;
        if (cleaned.Length > MaxSegmentLength)
        {
            cleaned = cleaned[..MaxSegmentLength].TrimEnd('-', '.');
            if (cleaned.Length == 0) return fallback;
        }
        if (WindowsReservedNames.Contains(cleaned))
        {
            cleaned = "_" + cleaned;
        }
        return cleaned;
    }

    /// <summary>Directory under the configured workspaces root holding one project's worktrees.</summary>
    public static string ProjectWorkspacesDirectory(
        string projectName,
        string projectPath,
        string? homeDirectory = null,
        Func<string, bool>? belongsToOtherProject = null,
        string? rootDirectory = null)
    {
        var root = WorkspacesRoot(homeDirectory, rootDirectory);
        var baseName = SanitizeSegment(string.IsNullOrWhiteSpace(projectName) ? System.IO.Path.GetFileName(projectPath.TrimEnd('/', '\\')) : projectName, "project");
        var candidate = System.IO.Path.Combine(root, baseName);
        if (belongsToOtherProject is not null && belongsToOtherProject(candidate))
        {
            var hash = ShortHash(projectPath);
            candidate = System.IO.Path.Combine(root, $"{baseName}-{hash}");
        }
        return candidate;
    }

    /// <summary>Builds the target worktree path <c>&lt;root&gt;/&lt;project&gt;/&lt;branch&gt;</c>, adding a numeric suffix on collision.</summary>
    public static string BuildWorkspacePath(
        string projectName,
        string projectPath,
        string branchName,
        string? homeDirectory = null,
        Func<string, bool>? pathExists = null,
        Func<string, bool>? belongsToOtherProject = null,
        string? rootDirectory = null)
    {
        var projectDir = ProjectWorkspacesDirectory(projectName, projectPath, homeDirectory, belongsToOtherProject, rootDirectory);
        return BuildWorkspacePathInDirectory(projectDir, branchName, pathExists);
    }

    /// <summary>Builds the target worktree path <c>&lt;baseDirectory&gt;/&lt;branch&gt;</c>, adding a numeric suffix on collision.</summary>
    public static string BuildWorkspacePathInDirectory(
        string baseDirectory,
        string branchName,
        Func<string, bool>? pathExists = null)
    {
        var branchSegment = SanitizeSegment(branchName, "branch");
        var exists = pathExists ?? Directory.Exists;
        var candidate = System.IO.Path.Combine(baseDirectory, branchSegment);
        if (!exists(candidate)) return candidate;
        for (var i = 2; i < 1000; i++)
        {
            var numbered = System.IO.Path.Combine(baseDirectory, $"{branchSegment}-{i}");
            if (!exists(numbered)) return numbered;
        }
        return System.IO.Path.Combine(baseDirectory, $"{branchSegment}-{Guid.NewGuid().ToString("N")[..6]}");
    }

    /// <summary>Formats an absolute path with <c>~</c> when it lives inside the user's home directory.</summary>
    public static string FormatHomePath(string path, string? homeDirectory = null)
    {
        var home = string.IsNullOrWhiteSpace(homeDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : homeDirectory;
        if (string.IsNullOrEmpty(home)) return path;
        var trimmedHome = home.TrimEnd('/', '\\');
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (path.Equals(trimmedHome, comparison)) return "~";
        if (path.StartsWith(trimmedHome + System.IO.Path.DirectorySeparatorChar, comparison)
            || path.StartsWith(trimmedHome + "/", comparison))
        {
            return "~" + path[trimmedHome.Length..];
        }
        return path;
    }

    private static string ShortHash(string value)
    {
        var normalized = value.Trim().TrimEnd('/', '\\');
        if (!OperatingSystem.IsLinux()) normalized = normalized.ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes[..3]).ToLowerInvariant();
    }
}
