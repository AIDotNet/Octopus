using System.Text;
using Avalonia.Threading;
using Octopus.Agents;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.Core.Agents;
using Octopus.Core.Projects;
using Octopus.Git;
using Octopus.Platform;
using Octopus.Storage;

namespace Octopus.App.Services.Workspaces;

public enum WorkspaceAfterCreate
{
    DefaultAgent,
    Terminal,
    None
}

public sealed record CreateWorkspaceRequest(
    bool CreateBranch,
    string BranchName,
    string? BaseRef,
    bool IsRemoteBranch,
    WorkspaceTaskLink? Task,
    bool InjectTaskPrompt,
    WorkspaceAfterCreate AfterCreate,
    string? CustomBaseDirectory = null);

public sealed record CreateWorkspaceResult(bool Succeeded, ProjectWorkspace? Workspace = null, string? Error = null);

public sealed record RemoveWorkspaceResult(bool Succeeded, bool BranchDeleted = false, string? Error = null);

/// <summary>Overrides the user home folder used to compute <c>~/.octopus/workspaces</c> (tests point this at a temp dir).</summary>
public sealed record WorkspaceRootOptions(string? HomeDirectory = null);

public interface IWorkspaceManager
{
    /// <summary>Home directory under which the default <c>.octopus/workspaces</c> lives.</summary>
    string HomeDirectory { get; }

    /// <summary>Built-in default root directory (<c>~/.octopus/workspaces</c>).</summary>
    string DefaultWorkspacesRootDirectory { get; }

    /// <summary>Effective root directory where new branch workspaces are created (custom setting or default).</summary>
    string WorkspacesRootDirectory { get; }

    /// <summary>Computes the target path <c>&lt;root&gt;/&lt;project&gt;/&lt;branch&gt;</c> for preview or creation.</summary>
    string ComputeWorkspacePath(Project project, string branchName);

    /// <summary>The directory under the configured workspaces root holding one project's worktrees.</summary>
    string ProjectWorkspacesDirectory(Project project);

    /// <summary>
    /// Synchronizes <see cref="Project.Workspaces"/> with <c>git worktree list --porcelain</c>: refreshes branch names
    /// renamed externally, adopts unmanaged worktrees living under <c>~/.octopus/workspaces/&lt;project&gt;/</c>, and drops
    /// entries whose folder was removed externally.
    /// </summary>
    Task SyncAsync(Project project, CancellationToken cancellationToken = default);

    /// <summary>Opens the "New branch workspace" dialog for the current local git project and executes creation if confirmed.</summary>
    Task<ProjectWorkspace?> PromptAndCreateAsync(Project? project = null, CancellationToken cancellationToken = default);

    /// <summary>Creates a git worktree for <paramref name="project"/>, records it, and optionally launches the initial tab.</summary>
    Task<CreateWorkspaceResult> CreateAsync(Project project, CreateWorkspaceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Confirms with the user (warning on uncommitted changes or open tabs), closes workspace tabs, removes the worktree and updates the project.</summary>
    Task<bool> RemoveWithConfirmAsync(Project project, ProjectWorkspace workspace, CancellationToken cancellationToken = default);

    /// <summary>Removes a worktree without prompting (used internally and in tests).</summary>
    Task<GitCommandResult> RemoveAsync(Project project, ProjectWorkspace workspace, bool force = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a worktree without prompting and optionally deletes its local branch when merged (never deletes a branch
    /// still checked out in another worktree).
    /// </summary>
    Task<RemoveWorkspaceResult> RemoveWorkspaceAsync(
        Project project,
        ProjectWorkspace workspace,
        bool force = false,
        bool deleteBranch = false,
        bool forceDeleteBranch = false,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the display name of the default agent offered in the dialog, or null when no agent is installed.</summary>
    string? DefaultAgentDisplayName();
}

public sealed class WorkspaceManager(
    IProjectService projects,
    IGitService git,
    IGitHubTaskProvider tasks,
    IDialogService dialogs,
    IItemOpener opener,
    IAgentCatalog catalog,
    IAgentScanner scanner,
    ISettingsStore settings,
    PathComparer paths,
    WorkspaceRootOptions? options = null) : IWorkspaceManager
{
    public string HomeDirectory => string.IsNullOrWhiteSpace(options?.HomeDirectory)
        ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        : options.HomeDirectory;

    public string DefaultWorkspacesRootDirectory => WorkspacePaths.DefaultWorkspacesRoot(HomeDirectory);

    public string WorkspacesRootDirectory => WorkspacePaths.WorkspacesRoot(HomeDirectory, settings.Current.Workspaces.RootDirectory);

    public string ProjectWorkspacesDirectory(Project project) =>
        WorkspacePaths.ProjectWorkspacesDirectory(project.Name, project.Path, HomeDirectory, BelongsToOtherProject(project), settings.Current.Workspaces.RootDirectory);

    public string ComputeWorkspacePath(Project project, string branchName) =>
        WorkspacePaths.BuildWorkspacePath(project.Name, project.Path, branchName, HomeDirectory, Directory.Exists, BelongsToOtherProject(project), settings.Current.Workspaces.RootDirectory);

    public string? DefaultAgentDisplayName()
    {
        var id = settings.Current.Agents.DefaultAgentId;
        if (id is null || scanner.Find(id) is null || !settings.Current.Agents.ProfileOf(id).Enabled)
        {
            id = scanner.Current.Installations
                .Select(i => i.AgentId)
                .FirstOrDefault(a => catalog.Find(a) is not null && settings.Current.Agents.ProfileOf(a).Enabled);
        }
        return id is null ? null : catalog.Find(id)?.Name;
    }

    public async Task SyncAsync(Project project, CancellationToken cancellationToken = default)
    {
        if (!ProjectLocation.IsOnThisComputer(project) || !Directory.Exists(project.Path)) return;
        var repoRoot = await git.FindRepositoryRootAsync(project.Path, cancellationToken).ConfigureAwait(false);
        if (repoRoot is null) return;
        var actual = await git.GetWorktreesAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        if (actual.Count == 0) return;

        var managedRoot = ProjectWorkspacesDirectory(project);
        var actualByPath = new Dictionary<string, GitWorktree>(paths);
        foreach (var wt in actual)
        {
            actualByPath[NormalizeReal(wt.Path)] = wt;
            actualByPath[wt.Path] = wt;
        }

        // Update existing records (branch renames) and drop entries removed externally.
        foreach (var ws in project.Workspaces.ToList())
        {
            if (actualByPath.TryGetValue(NormalizeReal(ws.Path), out var matched) || actualByPath.TryGetValue(ws.Path, out matched))
            {
                if (!string.IsNullOrWhiteSpace(matched.Branch) && ws.Branch != matched.Branch)
                {
                    var updated = new ProjectWorkspace
                    {
                        Id = ws.Id,
                        Name = ws.Name == ws.Branch ? matched.Branch : ws.Name,
                        Branch = matched.Branch,
                        BaseRef = ws.BaseRef,
                        Path = ws.Path,
                        Task = ws.Task,
                        IsCollapsed = ws.IsCollapsed,
                        IsPinned = ws.IsPinned,
                        InitialHead = ws.InitialHead,
                        LastActivityAt = ws.LastActivityAt,
                        CreatedAt = ws.CreatedAt,
                    };
                    UiContext.PostToUi(() => projects.UpdateWorkspace(project, updated));
                }
            }
            else if (!Directory.Exists(ws.Path))
            {
                var id = ws.Id;
                UiContext.PostToUi(() => projects.RemoveWorkspace(project, id));
            }
        }

        // Adopt linked worktrees that live under the configured (or default) managed workspaces directory for this project.
        var managedRoots = new List<string>
        {
            NormalizeReal(managedRoot).TrimEnd('/', '\\') + Path.DirectorySeparatorChar,
        };
        var defaultProjectDir = WorkspacePaths.ProjectWorkspacesDirectory(project.Name, project.Path, HomeDirectory, BelongsToOtherProject(project));
        var normalizedDefaultRoot = NormalizeReal(defaultProjectDir).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!managedRoots[0].Equals(normalizedDefaultRoot, pathComparison))
        {
            managedRoots.Add(normalizedDefaultRoot);
        }
        foreach (var wt in actual.Where(w => !w.IsMain && !w.IsBare && !w.IsPrunable))
        {
            var wtReal = NormalizeReal(wt.Path);
            if (!managedRoots.Any(root => wtReal.StartsWith(root, pathComparison)))
            {
                continue;
            }
            if (project.Workspaces.Any(w => paths.Equals(NormalizeReal(w.Path), wtReal) || paths.Equals(w.Path, wt.Path)))
            {
                continue;
            }
            var branch = wt.Branch ?? Path.GetFileName(wt.Path.TrimEnd('/', '\\'));
            var adopted = new ProjectWorkspace
            {
                Name = branch,
                Branch = branch,
                Path = wt.Path,
            };
            UiContext.PostToUi(() => projects.AddWorkspace(project, adopted));
        }
    }

    public async Task<ProjectWorkspace?> PromptAndCreateAsync(Project? project = null, CancellationToken cancellationToken = default)
    {
        project ??= projects.Current;
        if (project is null || !ProjectLocation.IsOnThisComputer(project) || !Directory.Exists(project.Path))
        {
            return null;
        }
        var repoRoot = await git.FindRepositoryRootAsync(project.Path, cancellationToken);
        if (repoRoot is null) return null;

        var branchesTask = git.GetBranchesAsync(repoRoot, cancellationToken);
        var worktreesTask = git.GetWorktreesAsync(repoRoot, cancellationToken);
        await Task.WhenAll(branchesTask, worktreesTask);

        var branches = branchesTask.Result;
        var worktrees = worktreesTask.Result;

        CreateWorkspaceRequest? previous = null;
        string? error = null;
        while (true)
        {
            var choice = await RunOnUiAsync(() => dialogs.AskCreateWorkspaceAsync(
                project,
                repoRoot,
                branches,
                worktrees,
                tasks,
                branch => ComputeWorkspacePath(project, branch),
                HomeDirectory,
                DefaultAgentDisplayName(),
                previous,
                error));
            if (choice is null) return null;

            var result = await CreateAsync(project, choice, cancellationToken);
            if (result.Succeeded) return result.Workspace;

            previous = choice;
            error = result.Error ?? "Failed to create git worktree.";
        }
    }

    public async Task<CreateWorkspaceResult> CreateAsync(Project project, CreateWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        if (!ProjectLocation.IsOnThisComputer(project) || !Directory.Exists(project.Path))
        {
            return new CreateWorkspaceResult(false, Error: "Branch workspaces are only available for local projects.");
        }
        var repoRoot = await git.FindRepositoryRootAsync(project.Path, cancellationToken).ConfigureAwait(false);
        if (repoRoot is null)
        {
            return new CreateWorkspaceResult(false, Error: "This project is not inside a git repository.");
        }

        var rawBranch = request.BranchName.Trim();
        var effectiveLocalBranch = !request.CreateBranch && request.IsRemoteBranch && rawBranch.IndexOf('/') is var slash and >= 0
            ? rawBranch[(slash + 1)..]
            : rawBranch;
        string targetPath;
        if (!string.IsNullOrWhiteSpace(request.CustomBaseDirectory))
        {
            if (!WorkspacePaths.TryResolveRootDirectory(request.CustomBaseDirectory, HomeDirectory, out var customBase))
            {
                return new CreateWorkspaceResult(false, Error: AppLangResources.Get(AppLangResourceKind.WorkspaceDirectoryErrorInvalid));
            }
            if (File.Exists(customBase))
            {
                return new CreateWorkspaceResult(false, Error: AppLangResources.Get(AppLangResourceKind.WorkspaceDirectoryErrorIsFile));
            }
            targetPath = WorkspacePaths.BuildWorkspacePathInDirectory(customBase, effectiveLocalBranch, Directory.Exists);
        }
        else
        {
            targetPath = ComputeWorkspacePath(project, effectiveLocalBranch);
        }

        var gitResult = await git.AddWorktreeAsync(
            repoRoot,
            targetPath,
            rawBranch,
            request.CreateBranch ? request.BaseRef : null,
            request.CreateBranch,
            request.IsRemoteBranch,
            cancellationToken).ConfigureAwait(false);
        if (!gitResult.Succeeded)
        {
            return new CreateWorkspaceResult(false, Error: gitResult.Error ?? "git worktree add failed.");
        }

        var initialHead = await git.GetHeadHashAsync(targetPath, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var workspace = new ProjectWorkspace
        {
            Name = effectiveLocalBranch,
            Branch = effectiveLocalBranch,
            BaseRef = request.CreateBranch ? request.BaseRef : null,
            Path = Path.GetFullPath(targetPath),
            Task = request.Task,
            InitialHead = initialHead,
            CreatedAt = now,
            LastActivityAt = now,
        };

        ProjectWorkspace saved = workspace;
        await RunOnUiAsync(() =>
        {
            saved = projects.AddWorkspace(project, workspace);
            LaunchInitialItem(saved, request);
        });
        return new CreateWorkspaceResult(true, saved);
    }

    public async Task<bool> RemoveWithConfirmAsync(Project project, ProjectWorkspace workspace, CancellationToken cancellationToken = default)
    {
        var dirtyCount = 0;
        if (Directory.Exists(workspace.Path))
        {
            var status = await git.GetStatusAsync(workspace.Path, cancellationToken);
            dirtyCount = status?.Changes.Select(c => c.Path).Distinct().Count() ?? 0;
        }
        var openItems = project.Items.Count(i => i.WorkspaceId == workspace.Id);

        var lines = new List<string>
        {
            AppLangResources.Format(AppLangResourceKind.WorkspaceRemoveBody, WorkspacePaths.FormatHomePath(workspace.Path, HomeDirectory)),
        };
        if (openItems > 0)
        {
            lines.Add(AppLangResources.Format(AppLangResourceKind.WorkspaceRemoveRunningWarning, openItems));
        }
        if (dirtyCount > 0)
        {
            lines.Add(AppLangResources.Format(AppLangResourceKind.WorkspaceRemoveDirtyWarning, dirtyCount));
        }

        var confirmed = await dialogs.ConfirmAsync(
            AppLangResources.Format(AppLangResourceKind.WorkspaceRemoveTitle, workspace.DisplayName),
            string.Join("\n\n", lines),
            AppLangResources.Get(dirtyCount > 0 ? AppLangResourceKind.WorkspaceRemoveForceConfirm : AppLangResourceKind.WorkspaceRemoveConfirm),
            isDanger: true);
        if (!confirmed) return false;

        var result = await RemoveAsync(project, workspace, force: dirtyCount > 0, cancellationToken);
        if (!result.Succeeded)
        {
            await dialogs.ConfirmAsync(
                AppLangResources.Get(AppLangResourceKind.WorkspaceRemoveFailedTitle),
                result.Error ?? "",
                AppLangResources.Get(AppLangResourceKind.CloseItem));
            return false;
        }
        return true;
    }

    public async Task<GitCommandResult> RemoveAsync(Project project, ProjectWorkspace workspace, bool force = false, CancellationToken cancellationToken = default)
    {
        var result = await RemoveWorkspaceAsync(project, workspace, force, deleteBranch: false, forceDeleteBranch: false, cancellationToken).ConfigureAwait(false);
        return result.Succeeded ? GitCommandResult.Ok : new GitCommandResult(false, result.Error);
    }

    public async Task<RemoveWorkspaceResult> RemoveWorkspaceAsync(
        Project project,
        ProjectWorkspace workspace,
        bool force = false,
        bool deleteBranch = false,
        bool forceDeleteBranch = false,
        CancellationToken cancellationToken = default)
    {
        var repoRoot = await git.FindRepositoryRootAsync(project.Path, cancellationToken).ConfigureAwait(false);
        if (repoRoot is not null && Directory.Exists(workspace.Path))
        {
            var removed = await git.RemoveWorktreeAsync(repoRoot, workspace.Path, force, cancellationToken).ConfigureAwait(false);
            if (!removed.Succeeded) return new RemoveWorkspaceResult(false, false, removed.Error);
        }
        else if (repoRoot is not null)
        {
            await git.PruneWorktreesAsync(repoRoot, cancellationToken).ConfigureAwait(false);
        }

        await RunOnUiAsync(() =>
        {
            foreach (var item in project.Items.Where(i => i.WorkspaceId == workspace.Id || IsFileInsideWorkspace(i, workspace.Path)).ToList())
            {
                projects.RemoveItem(item.Id);
            }
            projects.RemoveWorkspace(project, workspace.Id);
        });

        var branchDeleted = false;
        if (deleteBranch && repoRoot is not null && !string.IsNullOrWhiteSpace(workspace.Branch))
        {
            var worktrees = await git.GetWorktreesAsync(repoRoot, cancellationToken).ConfigureAwait(false);
            var stillCheckedOut = worktrees.Any(w => !w.IsBare && !w.IsPrunable && string.Equals(w.Branch, workspace.Branch, StringComparison.Ordinal));
            if (!stillCheckedOut)
            {
                var del = await git.DeleteBranchAsync(repoRoot, workspace.Branch, force: false, cancellationToken).ConfigureAwait(false);
                if (!del.Succeeded && forceDeleteBranch)
                {
                    del = await git.DeleteBranchAsync(repoRoot, workspace.Branch, force: true, cancellationToken).ConfigureAwait(false);
                }
                branchDeleted = del.Succeeded;
            }
        }

        return new RemoveWorkspaceResult(true, branchDeleted);
    }

    private static bool IsFileInsideWorkspace(ProjectItem item, string workspacePath)
    {
        if (item.Kind != ProjectItemKind.File || string.IsNullOrWhiteSpace(item.FilePath) || string.IsNullOrWhiteSpace(workspacePath))
        {
            return false;
        }
        var root = Path.GetFullPath(workspacePath).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        var file = Path.GetFullPath(item.FilePath);
        return file.StartsWith(root, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private static Task RunOnUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    private static Task<T> RunOnUiAsync<T>(Func<Task<T>> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);

    private void LaunchInitialItem(ProjectWorkspace workspace, CreateWorkspaceRequest request)
    {
        switch (request.AfterCreate)
        {
            case WorkspaceAfterCreate.DefaultAgent:
                var prompt = request.InjectTaskPrompt && request.Task is not null ? FormatTaskPrompt(request.Task) : null;
                var launch = prompt is null ? null : new AgentLaunchOptions { Prompt = prompt };
                if (opener.OpenDefaultAgent(workspace.Id, launch) is null)
                {
                    opener.OpenTerminal(workspace.Id);
                }
                break;
            case WorkspaceAfterCreate.Terminal:
                opener.OpenTerminal(workspace.Id);
                break;
        }
    }

    /// <summary>Builds a concise markdown task context block to seed the agent's initial prompt.</summary>
    public static string FormatTaskPrompt(WorkspaceTaskLink task)
    {
        var sb = new StringBuilder();
        var kind = task.Kind switch
        {
            WorkspaceTaskKind.GitHubIssue => "GitHub Issue",
            WorkspaceTaskKind.GitHubPr => "GitHub Pull Request",
            WorkspaceTaskKind.Url => "Linked Task",
            _ => "Task Note",
        };
        sb.Append(kind);
        if (!string.IsNullOrWhiteSpace(task.Key)) sb.Append(' ').Append(task.Key);
        if (!string.IsNullOrWhiteSpace(task.Title)) sb.Append(": ").Append(task.Title.Trim());
        if (!string.IsNullOrWhiteSpace(task.Url)) sb.AppendLine().Append("URL: ").Append(task.Url.Trim());
        if (!string.IsNullOrWhiteSpace(task.Body)) sb.AppendLine().AppendLine().Append(task.Body.Trim());
        return sb.ToString();
    }

    private Func<string, bool> BelongsToOtherProject(Project current) => candidate =>
    {
        var normalizedCandidate = Path.GetFullPath(candidate);
        return projects.Projects.Any(other =>
            other.Id != current.Id
            && ProjectLocation.IsOnThisComputer(other)
            && other.Workspaces.Any(w =>
                Path.GetDirectoryName(Path.GetFullPath(w.Path)) is { } parent
                && paths.Equals(parent, normalizedCandidate)));
    };

    private static string NormalizeReal(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (!Directory.Exists(full)) return full;
            var info = new DirectoryInfo(full);
            return info.ResolveLinkTarget(true)?.FullName ?? full;
        }
        catch (IOException)
        {
            return path;
        }
    }
}
