using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.Agents;
using Octopus.Agents.Sessions;
using Octopus.App.Composition;
using Octopus.App.Dialogs;
using Octopus.App.Explorer.Sessions;
using Octopus.App.Services;
using Octopus.App.Services.Workspaces;
using Octopus.App.Shell;
using Octopus.Core.Projects;
using Octopus.Git;
using Octopus.Platform;
using Octopus.Storage;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>End-to-end tests for git branch workspaces (Scheme C + grouping tree + multi-workspace sessions).</summary>
public class WorkspaceBranchTests
{
    private sealed class FakeGitHubTaskProvider : IGitHubTaskProvider
    {
        public TaskProviderResult Issues { get; set; } = new(true, null, []);
        public TaskProviderResult Prs { get; set; } = new(true, null, []);

        public Task<TaskProviderResult> ListIssuesAsync(string repositoryRoot, CancellationToken cancellationToken = default) => Task.FromResult(Issues);
        public Task<TaskProviderResult> ListPullRequestsAsync(string repositoryRoot, CancellationToken cancellationToken = default) => Task.FromResult(Prs);
    }

    private static string? FindGit()
    {
        var locator = new Octopus.Platform.Unix.UnixExecutableLocator();
        return locator.FindAll("git", Environment.GetEnvironmentVariable("PATH")).FirstOrDefault();
    }

    private static string CreateGitRepo(string name = "OctopusDemo")
    {
        var git = FindGit()!;
        var parent = Directory.CreateTempSubdirectory("octopus-ws-test-").FullName;
        var repo = Path.Combine(parent, name);
        Directory.CreateDirectory(repo);
        void Run(params string[] args)
        {
            var r = new ProcessRunner().RunAsync(git, ["-C", repo, "-c", "commit.gpgsign=false", "-c", "core.hooksPath=/dev/null", .. args]).GetAwaiter().GetResult();
            Assert.True(r.Succeeded, r.StandardError);
        }
        Run("init", "-q", "-b", "main");
        Run("config", "user.name", "Tester");
        Run("config", "user.email", "t@example.com");
        Run("config", "commit.gpgsign", "false");
        Run("config", "core.hooksPath", "/dev/null");
        File.WriteAllText(Path.Combine(repo, "root.txt"), "root\n");
        Run("add", ".");
        Run("commit", "-q", "-m", "init");
        return repo;
    }

    [AvaloniaFact, FreshApp]
    public async Task Create_workspace_places_worktree_under_octopus_workspaces_injects_task_prompt_and_groups_sidebar()
    {
        if (FindGit() is null) return;
        var repo = CreateGitRepo("OctopusDemo");
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var manager = AppHost.Get<IWorkspaceManager>();
            var opener = AppHost.Get<IItemOpener>();
            var shell = AppHost.Get<ShellViewModel>();

            var project = projects.AddProject(repo, "OctopusDemo");
            // Default directory terminal (no worktree selected)
            var defaultTerm = opener.OpenTerminal(IItemOpener.DefaultWorkspaceId)!;
            Assert.Null(defaultTerm.WorkspaceId);
            Assert.Null(defaultTerm.WorkingDirectory);
            Assert.False(shell.Panel.HasWorkspaces);

            // Create a new branch workspace with a bound GitHub Issue
            var task = new WorkspaceTaskLink
            {
                Kind = WorkspaceTaskKind.GitHubIssue,
                Key = "#142",
                Title = "Implement worktree workspaces",
                Url = "https://github.com/org/repo/issues/142",
                Body = "Place worktrees under ~/.octopus/workspaces/<project>/<branch>",
            };
            var created = await manager.CreateAsync(project, new CreateWorkspaceRequest(
                CreateBranch: true,
                BranchName: "feat/142-worktree-ui",
                BaseRef: "main",
                IsRemoteBranch: false,
                Task: task,
                InjectTaskPrompt: true,
                AfterCreate: WorkspaceAfterCreate.DefaultAgent));

            Assert.True(created.Succeeded, created.Error);
            var ws = Assert.IsType<ProjectWorkspace>(created.Workspace);
            Assert.Equal("feat/142-worktree-ui", ws.Branch);
            Assert.Equal(Path.Combine(manager.HomeDirectory, ".octopus", "workspaces", "OctopusDemo", "feat-142-worktree-ui"), ws.Path);
            Assert.True(Directory.Exists(ws.Path));

            // Initial agent launched in the worktree directory with the bound issue context as prompt.
            var agentItem = Assert.Single(project.Items, i => i.Kind == ProjectItemKind.Agent);
            Assert.Equal(ws.Id, agentItem.WorkspaceId);
            Assert.Equal(ws.Path, agentItem.WorkingDirectory);
            Assert.Contains("#142", agentItem.Launch?.Prompt ?? "");
            Assert.Contains("Implement worktree workspaces", agentItem.Launch?.Prompt ?? "");

            // Sidebar switches to grouping tree: Default group + worktree group.
            Assert.True(shell.Panel.HasWorkspaces);
            Assert.Equal(2, shell.Panel.WorkspaceGroups.Count);
            Assert.True(shell.Panel.WorkspaceGroups[0].IsDefault);
            Assert.Single(shell.Panel.WorkspaceGroups[0].Items);
            Assert.False(shell.Panel.WorkspaceGroups[1].IsDefault);
            Assert.Equal("feat/142-worktree-ui", shell.Panel.WorkspaceGroups[1].DisplayName);
            Assert.Equal("#142", shell.Panel.WorkspaceGroups[1].TaskKey);
            Assert.Single(shell.Panel.WorkspaceGroups[1].Items);

            // Shell tracks active tab's worktree directory for Files/Git while keeping project root for Sessions.
            Assert.Equal(ws.Path, shell.CurrentProjectPath);
            Assert.Equal(project.Path, shell.CurrentProjectRootPath);
            Assert.Equal("feat/142-worktree-ui", shell.CurrentWorkspaceBranch);

            // Switching active tab back to default terminal switches shell path back to default project directory.
            projects.ActivateItem(defaultTerm.Id);
            Assert.Equal(project.Path, shell.CurrentProjectPath);
            Assert.Null(shell.CurrentWorkspaceBranch);

            // Scheme C launcher targets the explicit workspace passed from a group's "+" button.
            shell.Launcher.SetTargetWorkspace(ws.Id, isGitProject: true);
            Assert.True(shell.Launcher.ShowWorkspaceBanner);
            Assert.True(shell.Launcher.IsTargetWorktree);
            shell.Launcher.NewTerminalCommand.Execute(null);
            var wsTerm = project.Items.Last();
            Assert.Equal(ws.Id, wsTerm.WorkspaceId);
            Assert.Equal(ws.Path, wsTerm.WorkingDirectory);

            // Removing the workspace closes its items and removes the worktree from disk.
            var removed = await manager.RemoveAsync(project, ws, force: true);
            Assert.True(removed.Succeeded, removed.Error);
            Assert.False(Directory.Exists(ws.Path));
            Assert.Empty(project.Workspaces);
            Assert.All(project.Items, i => Assert.Null(i.WorkspaceId));
            Assert.False(shell.Panel.HasWorkspaces);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(repo)!, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Create_workspace_honors_custom_default_root_and_per_workspace_custom_directory()
    {
        if (FindGit() is null) return;
        var repo = CreateGitRepo("CustomDirDemo");
        var customRoot = Directory.CreateTempSubdirectory("octopus-custom-root-").FullName;
        var oneOffBase = Directory.CreateTempSubdirectory("octopus-oneoff-base-").FullName;
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var manager = AppHost.Get<IWorkspaceManager>();
            var settings = AppHost.Get<ISettingsStore>();
            var project = projects.AddProject(repo, "CustomDirDemo");

            // 1. Configure custom global workspaces root directory.
            settings.Update(s => s.Workspaces.RootDirectory = customRoot);
            Assert.Equal(customRoot, manager.WorkspacesRootDirectory);

            var globalCreated = await manager.CreateAsync(project, new CreateWorkspaceRequest(
                CreateBranch: true,
                BranchName: "feat/custom-global",
                BaseRef: "main",
                IsRemoteBranch: false,
                Task: null,
                InjectTaskPrompt: false,
                AfterCreate: WorkspaceAfterCreate.None));
            Assert.True(globalCreated.Succeeded, globalCreated.Error);
            var globalWs = Assert.IsType<ProjectWorkspace>(globalCreated.Workspace);
            Assert.Equal(Path.Combine(customRoot, "CustomDirDemo", "feat-custom-global"), globalWs.Path);
            Assert.True(Directory.Exists(globalWs.Path));

            // 2. Override directory for a single workspace creation.
            var oneOffCreated = await manager.CreateAsync(project, new CreateWorkspaceRequest(
                CreateBranch: true,
                BranchName: "feat/one-off",
                BaseRef: "main",
                IsRemoteBranch: false,
                Task: null,
                InjectTaskPrompt: false,
                AfterCreate: WorkspaceAfterCreate.None,
                CustomBaseDirectory: oneOffBase));
            Assert.True(oneOffCreated.Succeeded, oneOffCreated.Error);
            var oneOffWs = Assert.IsType<ProjectWorkspace>(oneOffCreated.Workspace);
            Assert.Equal(Path.Combine(oneOffBase, "feat-one-off"), oneOffWs.Path);
            Assert.True(Directory.Exists(oneOffWs.Path));

            // 3. Resetting global root leaves existing workspaces intact.
            settings.Update(s => s.Workspaces.RootDirectory = null);
            await manager.SyncAsync(project);
            Assert.Equal(2, project.Workspaces.Count);

            // 4. PromptAndCreateAsync always invokes AskCreateWorkspaceAsync on the UI thread (even after async git calls).
            var dialogs = AppHost.Get<FakeDialogService>();
            dialogs.NextWorkspaceRequest = null;
            await manager.PromptAndCreateAsync(project);
            Assert.True(dialogs.LastWorkspaceDialogCalledOnUiThread);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(repo)!, true); } catch (IOException) { }
            try { Directory.Delete(customRoot, true); } catch (IOException) { }
            try { Directory.Delete(oneOffBase, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task Sessions_panel_aggregates_default_and_worktree_sessions_filters_and_resumes_in_original_workspace()
    {
        var root = Directory.CreateTempSubdirectory("octopus-sess-ws-").FullName;
        var defaultDir = Path.Combine(root, "repo");
        var wtDir = Path.Combine(root, "wt-feat");
        Directory.CreateDirectory(defaultDir);
        Directory.CreateDirectory(wtDir);
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var project = projects.AddProject(defaultDir, "Repo");
            var ws = projects.AddWorkspace(project, new ProjectWorkspace
            {
                Name = "feat/auth",
                Branch = "feat/auth",
                Path = wtDir,
            });

            var catalog = AppHost.Get<IAgentCatalog>();
            var claude = catalog.Find("claude")!;
            var now = DateTimeOffset.UtcNow;
            var fake = new FakeAgentSessionService();
            fake.GroupsByPath[("claude", defaultDir)] = new AgentSessionGroup(claude, true,
            [
                new AgentSessionAccount(FakeAgentSessionService.Team, "Team", true,
                    [new AgentSessionInfo("claude", FakeAgentSessionService.Team, "s-def", "Main branch fix", now.AddHours(-2), now.AddMinutes(-10), 14, "claude-opus-5-5", "main", "def.jsonl")]),
            ]);
            fake.GroupsByPath[("claude", wtDir)] = new AgentSessionGroup(claude, true,
            [
                new AgentSessionAccount(FakeAgentSessionService.Team, "Team", true,
                    [new AgentSessionInfo("claude", FakeAgentSessionService.Team, "s-wt", "Worktree auth refactor", now.AddHours(-1), now.AddMinutes(-2), 20, "claude-opus-5-5", "feat/auth", "wt.jsonl")]),
            ]);

            var panel = new SessionsPanelViewModel(
                fake, catalog, AppHost.Get<IAgentScanner>(), AppHost.Get<ISettingsStore>(), projects,
                AppHost.Get<IItemOpener>(), new NoopMigrationDialog(), AppHost.Get<IDialogService>(),
                AppHost.Get<IAppPaths>(), workspaces: AppHost.Get<IWorkspaceManager>());
            panel.SetProject(defaultDir);
            panel.IsActive = true;
            await panel.LoadAsync();

            var agentRow = Assert.Single(panel.Agents);
            Assert.Equal(2, agentRow.Group.Count);
            agentRow.OpenCommand.Execute(null);

            // Workspace filter chips appear: All, Default, Workspaces, feat/auth.
            Assert.True(panel.HasWorkspaceFilters);
            Assert.Equal(["all", "default", "workspaces", ws.Id], panel.WorkspaceFilters.Select(c => c.Key).ToArray());

            var allRows = panel.Groups.Single().Rows;
            Assert.Equal(2, allRows.Count);
            Assert.All(allRows, r => Assert.True(r.ShowWorkspaceBadge));

            // Filter to Default only
            panel.WorkspaceFilter = "default";
            var defRow = Assert.Single(panel.Groups.Single().Rows);
            Assert.Equal("s-def", defRow.Info.SessionId);
            Assert.False(defRow.IsWorktree);

            // Filter to Workspaces only and continue: opens in the worktree directory!
            panel.WorkspaceFilter = "workspaces";
            var wtRow = Assert.Single(panel.Groups.Single().Rows);
            Assert.Equal("s-wt", wtRow.Info.SessionId);
            Assert.True(wtRow.IsWorktree);
            Assert.Equal("feat/auth", wtRow.WorkspaceLabel);

            await wtRow.ContinueCommand.ExecuteAsync(null);
            var opened = Assert.Single(project.Items, i => i.Launch?.SessionId == "s-wt");
            Assert.Equal(ws.Id, opened.WorkspaceId);
            Assert.Equal(wtDir, opened.WorkingDirectory);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task CreateWorkspaceViewModel_loads_issues_suggests_branch_marks_occupied_branches_and_sanitizes_preview()
    {
        var provider = new FakeGitHubTaskProvider
        {
            Issues = new TaskProviderResult(true, null,
            [
                new RemoteTaskItem(WorkspaceTaskKind.GitHubIssue, 148, "#148", "Add worktree hooks", "https://github.com/org/repo/issues/148", "Support .env symlinks", "token", null, ["enhancement"]),
            ]),
        };
        var project = new Project { Name = "Octopus", Path = "/tmp/Octopus" };
        var branches = new List<GitBranch>
        {
            new("main", IsRemote: false, IsCurrent: true, "abc1234", null, null),
            new("feat/existing", IsRemote: false, IsCurrent: false, "def5678", null, null),
            new("origin/release/v1", IsRemote: true, IsCurrent: false, "9998887", null, null),
        };
        var worktrees = new List<GitWorktree>
        {
            new("/tmp/Octopus", "abc1234", "main", false, false, false, false, IsMain: true),
        };

        var customDir = Path.Combine(Path.GetTempPath(), "octopus-custom-worktrees");
        var vm = new CreateWorkspaceViewModel(
            project,
            project.Path,
            branches,
            worktrees,
            provider,
            branch => WorkspacePaths.BuildWorkspacePath(project.Name, project.Path, branch, "/home/u", _ => false),
            "/home/u",
            "Claude",
            pickFolder: () => Task.FromResult<string?>(customDir));

        // Empty branch name fails validation.
        Assert.Null(vm.BuildRequest());
        Assert.True(vm.HasError);

        // Occupied "main" cannot be selected in existing-branch mode.
        var mainOption = vm.FilteredBranches.Single(b => b.Name == "main");
        Assert.True(mainOption.IsOccupied);
        Assert.False(mainOption.CanSelect);

        // Switching to Issue tab loads issues and selecting one fills branch name & sanitized path preview.
        vm.TaskTab = "issue";
        Assert.True(Frames.Until(() => vm.FilteredTasks.Count == 1));
        vm.SelectTaskCommand.Execute(vm.FilteredTasks[0]);

        Assert.Equal("feat/148-add-worktree-hooks", vm.BranchName);
        Assert.Contains("feat-148-add-worktree-hooks", vm.PreviewPath);
        Assert.True(vm.HasSlashNote);

        // Changing directory overrides preview and populates CustomBaseDirectory on request; reset clears it.
        Assert.True(vm.CanChangeDirectory);
        await vm.ChangeDirectoryCommand.ExecuteAsync(null);
        Assert.True(vm.HasCustomBaseDirectory);
        Assert.Equal(Path.Combine(customDir, "feat-148-add-worktree-hooks"), vm.PreviewPath);
        Assert.Equal(customDir, vm.BuildRequest()?.CustomBaseDirectory);

        vm.ResetDirectoryCommand.Execute(null);
        Assert.False(vm.HasCustomBaseDirectory);

        var request = Assert.IsType<CreateWorkspaceRequest>(vm.BuildRequest());
        Assert.True(request.CreateBranch);
        Assert.Equal("feat/148-add-worktree-hooks", request.BranchName);
        Assert.Equal("main", request.BaseRef);
        Assert.Equal("#148", request.Task?.Key);
        Assert.True(request.InjectTaskPrompt);
        Assert.Null(request.CustomBaseDirectory);
    }

    [Fact]
    public void GhCliTaskProvider_parses_issue_and_pr_json_payloads()
    {
        var issueJson = """[{"number":42,"title":"Fix crash","url":"https://github.com/a/b/issues/42","body":"Details","author":{"login":"alice"},"labels":[{"name":"bug"}]}]""";
        var issues = GhCliTaskProvider.ParseItems(issueJson, WorkspaceTaskKind.GitHubIssue);
        var issue = Assert.Single(issues);
        Assert.Equal("#42", issue.Key);
        Assert.Equal("fix/42-fix-crash", issue.SuggestedBranch());

        var prJson = """[{"number":77,"title":"Refactor git","url":"https://github.com/a/b/pull/77","headRefName":"refactor/git-wt","author":{"login":"bob"},"labels":[]}]""";
        var prs = GhCliTaskProvider.ParseItems(prJson, WorkspaceTaskKind.GitHubPr);
        var pr = Assert.Single(prs);
        Assert.Equal("PR #77", pr.Key);
        Assert.Equal("refactor/git-wt", pr.SuggestedBranch());
    }

    [AvaloniaFact, FreshApp]
    public void Visual_tree_renders_workspace_groups_terminal_banner_and_create_dialog_without_binding_errors()
    {
        var root = Directory.CreateTempSubdirectory("octopus-ws-ui-").FullName;
        var defaultDir = Path.Combine(root, "Octopus");
        var wtDir = Path.Combine(root, "wt-142");
        Directory.CreateDirectory(defaultDir);
        Directory.CreateDirectory(wtDir);
        var window = new MainWindow { Width = 1280, Height = 760 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var opener = AppHost.Get<IItemOpener>();
            var project = projects.AddProject(defaultDir, "Octopus");
            var ws = projects.AddWorkspace(project, new ProjectWorkspace
            {
                Name = "feat/142-worktree",
                Branch = "feat/142-worktree",
                Path = wtDir,
                Task = new WorkspaceTaskLink { Kind = WorkspaceTaskKind.GitHubIssue, Key = "#142", Title = "Worktree support", Url = "https://github.com/org/repo/issues/142" },
            });
            opener.OpenTerminal(IItemOpener.DefaultWorkspaceId);
            opener.OpenAgent("claude", workspaceId: ws.Id);
            AppHost.Get<ShellViewModel>().IsRightPanelOpen = true;
            Frames.Pump();
            window.UpdateLayout();

            var groups = window.GetVisualDescendants().OfType<Octopus.App.Sidebar.WorkspaceGroupView>().ToList();
            Assert.Equal(2, groups.Count);

            var dialogVm = new CreateWorkspaceViewModel(
                project, defaultDir,
                [new GitBranch("main", false, true, "1234567", null, null)],
                [new GitWorktree(defaultDir, "1234567", "main", false, false, false, false, true)],
                new FakeGitHubTaskProvider(),
                b => Path.Combine(root, b),
                root,
                "Claude");
            var dialogWin = new Window { Width = 560, Height = 640, Content = new CreateWorkspaceView(dialogVm) };
            dialogWin.Show();
            try
            {
                Frames.Pump();
                dialogWin.UpdateLayout();
                Assert.True(dialogWin.Bounds.Height > 0);
            }
            finally
            {
                dialogWin.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private sealed class NoopMigrationDialog : ISessionMigrationDialog
    {
        public Task<SessionMigrationChoice?> AskAsync(SessionMigrationFormViewModel form) => Task.FromResult<SessionMigrationChoice?>(null);
    }
}
