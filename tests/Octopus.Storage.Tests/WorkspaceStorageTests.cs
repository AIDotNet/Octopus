using Octopus.Core.Projects;
using Octopus.Core.Settings;
using Octopus.Storage;
using Xunit;

namespace Octopus.Storage.Tests;

public class WorkspaceStorageTests
{
    [Fact]
    public async Task Workspaces_and_task_links_round_trip_and_normalize_dangling_ids()
    {
        using var paths = new TempPaths();
        var store = new JsonProjectStore(paths);

        var ws = new ProjectWorkspace
        {
            Id = "ws1",
            Name = "feat/worktree-ui",
            Branch = "feat/worktree-ui",
            BaseRef = "main",
            Path = "/Users/u/.octopus/workspaces/Octopus/feat-worktree-ui",
            IsCollapsed = true,
            Task = new WorkspaceTaskLink
            {
                Kind = WorkspaceTaskKind.GitHubIssue,
                Key = "#142",
                Title = "Support git worktree workspaces",
                Url = "https://github.com/org/repo/issues/142",
                Body = "Details here",
            },
        };
        var validItem = new ProjectItem { Id = "i1", Kind = ProjectItemKind.Agent, Title = "Claude", AgentId = "claude", WorkspaceId = "ws1", WorkingDirectory = ws.Path };
        var danglingItem = new ProjectItem { Id = "i2", Kind = ProjectItemKind.Terminal, Title = "zsh", WorkspaceId = "gone-ws" };

        var doc = new ProjectsDocument
        {
            ActiveProjectId = "p1",
            Projects =
            [
                new Project
                {
                    Id = "p1",
                    Name = "Octopus",
                    Path = "/Users/u/code/Octopus",
                    Workspaces = [ws],
                    Items = [validItem, danglingItem],
                },
            ],
        };

        await store.SaveAsync(doc, TestContext.Current.CancellationToken);
        var loaded = await new JsonProjectStore(paths).LoadAsync(TestContext.Current.CancellationToken);

        var project = Assert.Single(loaded.Projects);
        var loadedWs = Assert.Single(project.Workspaces);
        Assert.Equal("ws1", loadedWs.Id);
        Assert.Equal("feat/worktree-ui", loadedWs.Branch);
        Assert.Equal("main", loadedWs.BaseRef);
        Assert.True(loadedWs.IsCollapsed);
        Assert.NotNull(loadedWs.Task);
        Assert.Equal(WorkspaceTaskKind.GitHubIssue, loadedWs.Task!.Kind);
        Assert.Equal("#142", loadedWs.Task.Key);
        Assert.Equal("https://github.com/org/repo/issues/142", loadedWs.Task.Url);

        Assert.Equal("ws1", project.Items[0].WorkspaceId);
        Assert.Null(project.Items[1].WorkspaceId);
    }

    [Fact]
    public void WorkspacePaths_sanitizes_slashes_and_resolves_collisions_under_home()
    {
        var home = Path.Combine(Path.GetTempPath(), "octopus-home-test");
        Assert.Equal("feat-142-worktree-ui", WorkspacePaths.SanitizeSegment("feat/142/worktree ui"));
        Assert.Equal("_CON", WorkspacePaths.SanitizeSegment("CON"));

        var path = WorkspacePaths.BuildWorkspacePath("Octopus", "/code/Octopus", "feat/142-hooks", home, _ => false);
        Assert.Equal(Path.Combine(home, ".octopus", "workspaces", "Octopus", "feat-142-hooks"), path);

        // Existing path collision appends numeric suffix.
        var existing = new HashSet<string>(StringComparer.Ordinal) { path };
        var next = WorkspacePaths.BuildWorkspacePath("Octopus", "/code/Octopus", "feat/142-hooks", home, existing.Contains);
        Assert.Equal(Path.Combine(home, ".octopus", "workspaces", "Octopus", "feat-142-hooks-2"), next);

        // Two different projects sharing the same name get disambiguated by short hash.
        var disambiguated = WorkspacePaths.ProjectWorkspacesDirectory("app", "/work/team-b/app", home, _ => true);
        Assert.StartsWith(Path.Combine(home, ".octopus", "workspaces", "app-"), disambiguated);

        // Custom workspaces root (absolute and tilde-prefixed) overrides ~/.octopus/workspaces.
        var customRoot = Path.Combine(Path.GetTempPath(), "octopus-custom-ws");
        Assert.Equal(customRoot, WorkspacePaths.WorkspacesRoot(home, customRoot));
        Assert.Equal(Path.Combine(home, "worktrees"), WorkspacePaths.WorkspacesRoot(home, "~/worktrees"));
        Assert.Equal(Path.Combine(customRoot, "Octopus", "feat-custom"), WorkspacePaths.BuildWorkspacePath("Octopus", "/code/Octopus", "feat/custom", home, _ => false, rootDirectory: customRoot));

        // Relative or malformed roots are rejected and fall back to default.
        Assert.False(WorkspacePaths.TryResolveRootDirectory("relative/worktrees", home, out _));
        Assert.Equal(Path.Combine(home, ".octopus", "workspaces"), WorkspacePaths.WorkspacesRoot(home, "relative/worktrees"));

        // Direct custom parent directory collision handling.
        var oneOff = WorkspacePaths.BuildWorkspacePathInDirectory(customRoot, "feat/one-off", _ => false);
        Assert.Equal(Path.Combine(customRoot, "feat-one-off"), oneOff);
        Assert.Equal(Path.Combine(customRoot, "feat-one-off-2"), WorkspacePaths.BuildWorkspacePathInDirectory(customRoot, "feat/one-off", p => p == oneOff));
    }

    [Fact]
    public async Task Workspace_pin_activity_and_cleanup_settings_round_trip_and_normalize()
    {
        using var paths = new TempPaths();
        var projectStore = new JsonProjectStore(paths);
        var activityAt = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

        var doc = new ProjectsDocument
        {
            Projects =
            [
                new Project
                {
                    Id = "p1",
                    Name = "Octopus",
                    Path = "/code/Octopus",
                    Workspaces =
                    [
                        new ProjectWorkspace
                        {
                            Id = "ws-pin",
                            Name = "feat/pinned",
                            Branch = "feat/pinned",
                            Path = "/home/u/.octopus/workspaces/Octopus/feat-pinned",
                            IsPinned = true,
                            InitialHead = "1234567890abcdef1234567890abcdef12345678",
                            LastActivityAt = activityAt,
                        },
                    ],
                },
            ],
        };

        await projectStore.SaveAsync(doc, TestContext.Current.CancellationToken);
        var loadedDoc = await new JsonProjectStore(paths).LoadAsync(TestContext.Current.CancellationToken);
        var loadedWs = Assert.Single(Assert.Single(loadedDoc.Projects).Workspaces);
        Assert.True(loadedWs.IsPinned);
        Assert.Equal("1234567890abcdef1234567890abcdef12345678", loadedWs.InitialHead);
        Assert.Equal(activityAt, loadedWs.LastActivityAt);

        var settingsStore = new JsonSettingsStore(paths);
        settingsStore.Load();
        Assert.False(settingsStore.Current.WorkspaceCleanup.HasAutoRulesEnabled());
        Assert.Equal(30, settingsStore.Current.WorkspaceCleanup.InactiveDays);
        Assert.True(settingsStore.Current.WorkspaceCleanup.DeleteMergedBranch);

        settingsStore.Update(s =>
        {
            s.Workspaces.RootDirectory = "  ~/custom-worktrees  ";
            s.WorkspaceCleanup.DeleteMerged = true;
            s.WorkspaceCleanup.DeleteInactive = true;
            s.WorkspaceCleanup.InactiveDays = 999; // clamped to 365
            s.WorkspaceCleanup.CheckIntervalMinutes = 5; // clamped to 15
            s.WorkspaceCleanup.UnchangedGraceHours = 500; // clamped to 168
            s.WorkspaceCleanup.DeleteMergedBranch = false;
            s.WorkspaceCleanup.FetchBeforeCheck = true;
        });
        settingsStore.Flush();

        var reloaded = new JsonSettingsStore(paths);
        reloaded.Load();
        Assert.Equal("~/custom-worktrees", reloaded.Current.Workspaces.RootDirectory);
        Assert.True(reloaded.Current.WorkspaceCleanup.DeleteMerged);
        Assert.True(reloaded.Current.WorkspaceCleanup.DeleteInactive);
        Assert.True(reloaded.Current.WorkspaceCleanup.HasAutoRulesEnabled());
        Assert.Equal(365, reloaded.Current.WorkspaceCleanup.InactiveDays);
        Assert.Equal(15, reloaded.Current.WorkspaceCleanup.CheckIntervalMinutes);
        Assert.Equal(168, reloaded.Current.WorkspaceCleanup.UnchangedGraceHours);
        Assert.False(reloaded.Current.WorkspaceCleanup.DeleteMergedBranch);
        Assert.True(reloaded.Current.WorkspaceCleanup.FetchBeforeCheck);

        // Blank root directory normalizes back to null.
        reloaded.Update(s => s.Workspaces.RootDirectory = "   ");
        reloaded.Flush();
        var reloadedDefault = new JsonSettingsStore(paths);
        reloadedDefault.Load();
        Assert.Null(reloadedDefault.Current.Workspaces.RootDirectory);
    }
}
