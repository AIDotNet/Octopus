using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.App.Activity;
using Octopus.App.Composition;
using Octopus.App.Dialogs;
using Octopus.App.Services;
using Octopus.App.Services.Workspaces;
using Octopus.App.Settings;
using Octopus.App.Settings.Workspaces;
using Octopus.App.Workspace;
using Octopus.Core.Projects;
using Octopus.Git;
using Octopus.Platform;
using Octopus.Storage;
using Octopus.Terminal.Avalonia;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>End-to-end tests for automatic and manual git worktree workspace cleanup, protections, and settings UI.</summary>
public class WorkspaceCleanupTests
{
    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class StubGitHubTasks(IReadOnlyList<PullRequestMergeInfo> mergedPrs) : IGitHubTaskProvider
    {
        public Task<TaskProviderResult> ListIssuesAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TaskProviderResult(true, null, []));

        public Task<TaskProviderResult> ListPullRequestsAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TaskProviderResult(true, null, []));

        public Task<IReadOnlyList<PullRequestMergeInfo>> ListMergedPullRequestsAsync(string repositoryRoot, CancellationToken cancellationToken = default) =>
            Task.FromResult(mergedPrs);
    }

    private sealed class FakeTabContent(bool unsaved = false, bool running = false) : ITabContent
    {
        public Task<bool> HasRunningChildAsync() => Task.FromResult(running);
        public Task<bool> HasUnsavedChangesAsync() => Task.FromResult(unsaved);
        public Task CloseAsync() => Task.CompletedTask;
        public Task RestartAsync() => Task.CompletedTask;
        public void FocusContent() { }
    }

    private sealed class FakeTabRegistry : ITabContentRegistry
    {
        public Dictionary<string, ITabContent> Tabs { get; } = [];
        public Control GetOrCreate(Project project, ProjectItem item) => new Border();
        public Control GetOrCreateDiff(Octopus.App.Files.DiffTabRequest request) => new Border();
        public ITabContent? Find(string itemId) => Tabs.GetValueOrDefault(itemId);
        public void Detach(string itemId) { }
        public Func<Task>? CaptureForRelease(string itemId) =>
            Tabs.Remove(itemId, out var tab) ? tab.CloseAsync : null;
        public Task WaitForReleaseAsync(string itemId) => Task.CompletedTask;
        public Task ReleaseAsync(string itemId) => CaptureForRelease(itemId)?.Invoke() ?? Task.CompletedTask;
        public Task ReleaseAllAsync() { Tabs.Clear(); return Task.CompletedTask; }
        public Task<bool> AnyRunningChildAsync() => Task.FromResult(false);
        public Task<bool> AnyUnsavedChangesAsync() => Task.FromResult(false);
    }

    private static string? FindGit()
    {
        var locator = new Octopus.Platform.Unix.UnixExecutableLocator();
        return locator.FindAll("git", Environment.GetEnvironmentVariable("PATH")).FirstOrDefault();
    }

    private static void Git(string dir, params string[] args)
    {
        var git = FindGit()!;
        var r = new ProcessRunner().RunAsync(git, ["-C", dir, "-c", "commit.gpgsign=false", "-c", "core.hooksPath=/dev/null", .. args]).GetAwaiter().GetResult();
        Assert.True(r.Succeeded, r.StandardError);
    }

    private static (string Root, string Repo, string BareRemote) CreateGitRepoWithRemote(string name = "CleanupDemo")
    {
        var root = Directory.CreateTempSubdirectory("octopus-cleanup-test-").FullName;
        var repo = Path.Combine(root, name);
        var bare = Path.Combine(root, "origin.git");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(bare);

        Git(bare, "init", "--bare", "-q");
        Git(repo, "init", "-q", "-b", "main");
        Git(repo, "config", "user.name", "Tester");
        Git(repo, "config", "user.email", "t@example.com");
        Git(repo, "config", "commit.gpgsign", "false");
        Git(repo, "config", "core.hooksPath", "/dev/null");
        File.WriteAllText(Path.Combine(repo, "README.md"), "hello\n");
        Git(repo, "add", ".");
        Git(repo, "commit", "-q", "-m", "init");
        Git(repo, "remote", "add", "origin", bare);
        Git(repo, "push", "-q", "-u", "origin", "main");
        Git(repo, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");

        return (root, repo, bare);
    }

    private static WorkspaceCleanupService CreateCleanupService(
        IGitHubTaskProvider? tasks = null,
        ITabContentRegistry? tabs = null,
        TimeProvider? time = null) =>
        new(
            AppHost.Get<IProjectService>(),
            AppHost.Get<IWorkspaceManager>(),
            AppHost.Get<IGitService>(),
            tasks ?? new StubGitHubTasks([]),
            AppHost.Get<ISettingsStore>(),
            AppHost.Get<IItemActivityService>(),
            tabs ?? AppHost.Get<ITabContentRegistry>(),
            AppHost.Get<TerminalSessionRegistry>(),
            options: new WorkspaceCleanupOptions { AutoStart = false, TimeProvider = time });

    [AvaloniaFact, FreshApp]
    public async Task AutoCleanup_removes_merged_workspaces_by_git_and_github_pr_and_deletes_merged_branches()
    {
        if (FindGit() is null) return;
        var (root, repo, _) = CreateGitRepoWithRemote();
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var manager = AppHost.Get<IWorkspaceManager>();
            var git = AppHost.Get<IGitService>();
            var settings = AppHost.Get<ISettingsStore>();
            var project = projects.AddProject(repo, "CleanupDemo");

            // Workspace 1: merged via --no-ff into main and pushed.
            var wsGitMerged = (await manager.CreateAsync(project, new CreateWorkspaceRequest(true, "feat/git-merged", "main", false, null, false, WorkspaceAfterCreate.None))).Workspace!;
            File.WriteAllText(Path.Combine(wsGitMerged.Path, "a.txt"), "a\n");
            Git(wsGitMerged.Path, "add", ".");
            Git(wsGitMerged.Path, "commit", "-q", "-m", "add a");
            Git(repo, "merge", "--no-ff", "-q", "-m", "merge a", "feat/git-merged");
            Git(repo, "push", "-q", "origin", "main");

            // Workspace 2: PR squash-merged on GitHub (not yet fetched locally, verified by PR headRefOid == HEAD).
            var wsPrMerged = (await manager.CreateAsync(project, new CreateWorkspaceRequest(true, "feat/pr-squashed", "main", false, null, false, WorkspaceAfterCreate.None))).Workspace!;
            File.WriteAllText(Path.Combine(wsPrMerged.Path, "b.txt"), "b\n");
            Git(wsPrMerged.Path, "add", ".");
            Git(wsPrMerged.Path, "commit", "-q", "-m", "add b");
            var prHeadSha = await git.GetHeadHashAsync(wsPrMerged.Path);

            // Workspace 3: PR merged earlier, but user added a NEW commit afterwards (must NOT be removed!).
            var wsPostMergeCommit = (await manager.CreateAsync(project, new CreateWorkspaceRequest(true, "feat/post-merge", "main", false, null, false, WorkspaceAfterCreate.None))).Workspace!;
            File.WriteAllText(Path.Combine(wsPostMergeCommit.Path, "c.txt"), "c1\n");
            Git(wsPostMergeCommit.Path, "add", ".");
            Git(wsPostMergeCommit.Path, "commit", "-q", "-m", "c1");
            var oldMergedSha = await git.GetHeadHashAsync(wsPostMergeCommit.Path);
            File.WriteAllText(Path.Combine(wsPostMergeCommit.Path, "c2.txt"), "c2\n");
            Git(wsPostMergeCommit.Path, "add", ".");
            Git(wsPostMergeCommit.Path, "commit", "-q", "-m", "extra commit after merge");

            settings.Update(s =>
            {
                s.WorkspaceCleanup.DeleteMerged = true;
                s.WorkspaceCleanup.DeleteMergedBranch = true;
            });

            using var cleanup = CreateCleanupService(tasks: new StubGitHubTasks(
            [
                new PullRequestMergeInfo(101, "feat/pr-squashed", prHeadSha, DateTimeOffset.UtcNow.AddMinutes(-5), "https://github.com/org/repo/pull/101"),
                new PullRequestMergeInfo(102, "feat/post-merge", oldMergedSha, DateTimeOffset.UtcNow.AddMinutes(-10), "https://github.com/org/repo/pull/102"),
            ]));

            var summary = await cleanup.RunAutoCleanupAsync(isAutomatic: false);

            Assert.Equal(3, summary.InspectedCount);
            Assert.Equal(2, summary.RemovedCount);
            Assert.Equal(2, summary.BranchesDeletedCount);
            Assert.False(Directory.Exists(wsGitMerged.Path));
            Assert.False(Directory.Exists(wsPrMerged.Path));
            Assert.True(Directory.Exists(wsPostMergeCommit.Path));

            var remaining = Assert.Single(project.Workspaces);
            Assert.Equal(wsPostMergeCommit.Id, remaining.Id);

            // Merged local branches were deleted; unmerged branch stays.
            var branches = await git.GetBranchesAsync(repo);
            Assert.DoesNotContain(branches, b => !b.IsRemote && b.Name == "feat/git-merged");
            Assert.DoesNotContain(branches, b => !b.IsRemote && b.Name == "feat/pr-squashed");
            Assert.Contains(branches, b => !b.IsRemote && b.Name == "feat/post-merge");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task AutoCleanup_respects_grace_period_for_unchanged_and_keeps_branch_for_unchanged_and_inactive()
    {
        if (FindGit() is null) return;
        var (root, repo, _) = CreateGitRepoWithRemote();
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var manager = AppHost.Get<IWorkspaceManager>();
            var git = AppHost.Get<IGitService>();
            var settings = AppHost.Get<ISettingsStore>();
            var project = projects.AddProject(repo, "CleanupDemo");

            // Unchanged workspace (0 commits ahead of main).
            var wsUnchanged = (await manager.CreateAsync(project, new CreateWorkspaceRequest(true, "feat/empty-idea", "main", false, null, false, WorkspaceAfterCreate.None))).Workspace!;

            // Inactive workspace with a pushed commit (so not unpushed, and not merged).
            var wsInactive = (await manager.CreateAsync(project, new CreateWorkspaceRequest(true, "feat/old-work", "main", false, null, false, WorkspaceAfterCreate.None))).Workspace!;
            File.WriteAllText(Path.Combine(wsInactive.Path, "old.txt"), "old\n");
            Git(wsInactive.Path, "add", ".");
            Git(wsInactive.Path, "commit", "-q", "-m", "pushed work");
            Git(wsInactive.Path, "push", "-q", "-u", "origin", "feat/old-work");

            settings.Update(s =>
            {
                s.WorkspaceCleanup.DeleteUnchanged = true;
                s.WorkspaceCleanup.DeleteInactive = true;
                s.WorkspaceCleanup.InactiveDays = 30;
                s.WorkspaceCleanup.UnchangedGraceHours = 2;
                s.WorkspaceCleanup.DeleteMergedBranch = true;
            });

            // Immediately after creation: unchanged is protected by grace period, inactive is < 30 days old -> nothing removed.
            var fakeTime = new FakeTime(DateTimeOffset.UtcNow.AddMinutes(5));
            using var cleanup = CreateCleanupService(time: fakeTime);
            var early = await cleanup.RunAutoCleanupAsync();
            Assert.Equal(0, early.RemovedCount);
            Assert.Equal(2, project.Workspaces.Count);

            // Advance 3 hours: unchanged workspace passes grace period and is removed (branch kept); inactive still < 30d.
            fakeTime.UtcNow = DateTimeOffset.UtcNow.AddHours(3);
            var afterGrace = await cleanup.RunAutoCleanupAsync();
            Assert.Equal(1, afterGrace.RemovedCount);
            Assert.Equal(0, afterGrace.BranchesDeletedCount);
            Assert.False(Directory.Exists(wsUnchanged.Path));
            Assert.True(Directory.Exists(wsInactive.Path));

            // Advance 35 days: inactive workspace is removed, and its unmerged local branch is KEPT.
            fakeTime.UtcNow = DateTimeOffset.UtcNow.AddDays(35);
            var afterInactive = await cleanup.RunAutoCleanupAsync();
            Assert.Equal(1, afterInactive.RemovedCount);
            Assert.Equal(0, afterInactive.BranchesDeletedCount);
            Assert.False(Directory.Exists(wsInactive.Path));
            Assert.Empty(project.Workspaces);

            var branches = await git.GetBranchesAsync(repo);
            Assert.Contains(branches, b => !b.IsRemote && b.Name == "feat/empty-idea");
            Assert.Contains(branches, b => !b.IsRemote && b.Name == "feat/old-work");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task AutoCleanup_always_protects_pinned_running_unsaved_uncommitted_and_unpushed_workspaces()
    {
        if (FindGit() is null) return;
        var (root, repo, _) = CreateGitRepoWithRemote();
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var manager = AppHost.Get<IWorkspaceManager>();
            var opener = AppHost.Get<IItemOpener>();
            var settings = AppHost.Get<ISettingsStore>();
            var project = projects.AddProject(repo, "CleanupDemo");

            // 1. Pinned unchanged workspace.
            var wsPinned = (await manager.CreateAsync(project, new CreateWorkspaceRequest(true, "feat/pinned", "main", false, null, false, WorkspaceAfterCreate.None))).Workspace!;
            projects.SetWorkspacePinned(project, wsPinned.Id, true);

            // 2. Running terminal workspace.
            var wsRunning = (await manager.CreateAsync(project, new CreateWorkspaceRequest(true, "feat/running", "main", false, null, false, WorkspaceAfterCreate.Terminal))).Workspace!;

            // 3. Workspace with unsaved editor file tab (file inside worktree, WorkspaceId null as OpenFile creates it).
            var wsUnsaved = (await manager.CreateAsync(project, new CreateWorkspaceRequest(true, "feat/unsaved", "main", false, null, false, WorkspaceAfterCreate.None))).Workspace!;
            var fileItem = opener.OpenFile(Path.Combine(wsUnsaved.Path, "README.md"))!;
            var fakeTabs = new FakeTabRegistry();
            fakeTabs.Tabs[fileItem.Id] = new FakeTabContent(unsaved: true);

            // 4. Workspace with uncommitted changes.
            var wsDirty = (await manager.CreateAsync(project, new CreateWorkspaceRequest(true, "feat/dirty", "main", false, null, false, WorkspaceAfterCreate.None))).Workspace!;
            File.WriteAllText(Path.Combine(wsDirty.Path, "uncommitted.txt"), "draft\n");

            // 5. Workspace with unpushed local commit (even if > 30 days inactive!).
            var wsUnpushed = (await manager.CreateAsync(project, new CreateWorkspaceRequest(true, "feat/unpushed", "main", false, null, false, WorkspaceAfterCreate.None))).Workspace!;
            File.WriteAllText(Path.Combine(wsUnpushed.Path, "local.txt"), "commit\n");
            Git(wsUnpushed.Path, "add", ".");
            Git(wsUnpushed.Path, "commit", "-q", "-m", "unpushed");

            settings.Update(s =>
            {
                s.WorkspaceCleanup.DeleteMerged = true;
                s.WorkspaceCleanup.DeleteUnchanged = true;
                s.WorkspaceCleanup.DeleteInactive = true;
                s.WorkspaceCleanup.InactiveDays = 7;
                s.WorkspaceCleanup.UnchangedGraceHours = 0;
            });

            var fakeTime = new FakeTime(DateTimeOffset.UtcNow.AddDays(40));
            using var cleanup = CreateCleanupService(tabs: fakeTabs, time: fakeTime);

            var candidates = await cleanup.InspectAllAsync();
            Assert.Equal(5, candidates.Count);
            Assert.True(candidates.Single(c => c.Workspace.Id == wsPinned.Id).Protections.HasFlag(WorkspaceProtectionReason.Pinned));
            Assert.True(candidates.Single(c => c.Workspace.Id == wsRunning.Id).Protections.HasFlag(WorkspaceProtectionReason.RunningSession));
            Assert.True(candidates.Single(c => c.Workspace.Id == wsUnsaved.Id).Protections.HasFlag(WorkspaceProtectionReason.UnsavedEditorChanges));
            Assert.True(candidates.Single(c => c.Workspace.Id == wsDirty.Id).Protections.HasFlag(WorkspaceProtectionReason.UncommittedChanges));
            Assert.True(candidates.Single(c => c.Workspace.Id == wsUnpushed.Id).Protections.HasFlag(WorkspaceProtectionReason.UnpushedCommits));
            Assert.All(candidates, c => Assert.False(c.CanAutoClean));

            var summary = await cleanup.RunAutoCleanupAsync();
            Assert.Equal(0, summary.RemovedCount);
            Assert.Equal(5, project.Workspaces.Count);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task ManualCleanup_rechecks_and_skips_when_unsaved_work_appears_before_removal()
    {
        if (FindGit() is null) return;
        var (root, repo, _) = CreateGitRepoWithRemote();
        try
        {
            var projects = AppHost.Get<IProjectService>();
            var manager = AppHost.Get<IWorkspaceManager>();
            var project = projects.AddProject(repo, "CleanupDemo");

            var ws = (await manager.CreateAsync(project, new CreateWorkspaceRequest(true, "feat/race", "main", false, null, false, WorkspaceAfterCreate.None))).Workspace!;

            using var cleanup = CreateCleanupService();
            var candidates = await cleanup.InspectAllAsync();
            var candidate = Assert.Single(candidates);
            Assert.False(candidate.IsProtected);

            // Simulate user creating an uncommitted file after inspecting, right before confirming delete.
            File.WriteAllText(Path.Combine(ws.Path, "new-work.txt"), "important\n");

            var summary = await cleanup.CleanSelectedAsync([new WorkspaceCleanupSelection(project, ws, AllowForceRemoval: false)]);
            Assert.Equal(0, summary.RemovedCount);
            Assert.Equal(1, summary.SkippedCount);
            Assert.True(Directory.Exists(ws.Path));
            Assert.Single(project.Workspaces);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void GhCliTaskProvider_parses_merged_pull_request_payload()
    {
        var json = """[{"number":88,"headRefName":"feat/cleanup","headRefOid":"abcdef1234567890","mergedAt":"2026-11-20T08:30:00Z","url":"https://github.com/org/repo/pull/88"}]""";
        var merged = GhCliTaskProvider.ParseMergedPullRequests(json);
        var pr = Assert.Single(merged);
        Assert.Equal(88, pr.Number);
        Assert.Equal("feat/cleanup", pr.HeadBranch);
        Assert.Equal("abcdef1234567890", pr.HeadRefOid);
        Assert.NotNull(pr.MergedAt);
    }

    private sealed class StubPathPicker(string? picked) : Octopus.App.Settings.Agents.IPathPicker
    {
        public string? Picked { get; set; } = picked;
        public Task<string?> PickAsync(bool folder, string title) => Task.FromResult(Picked);
    }

    [AvaloniaFact, FreshApp]
    public async Task WorkspaceCleanupSettings_supports_custom_root_directory_validation_browse_and_reset()
    {
        var settings = AppHost.Get<ISettingsStore>();
        var cleanup = AppHost.Get<IWorkspaceCleanupService>();
        var projects = AppHost.Get<IProjectService>();
        var dialogs = AppHost.Get<FakeDialogService>();
        var workspaces = AppHost.Get<IWorkspaceManager>();
        var tempRoot = Directory.CreateTempSubdirectory("octopus-ws-dir-setting-").FullName;
        var customDir = Path.Combine(tempRoot, "my-worktrees");
        var pickedDir = Path.Combine(tempRoot, "picked-worktrees");
        var existingFile = Path.Combine(tempRoot, "not-a-folder.txt");
        File.WriteAllText(existingFile, "x");
        try
        {
            var picker = new StubPathPicker(pickedDir);
            using var vm = new WorkspaceCleanupSettingsViewModel(settings, cleanup, projects, dialogs, fileOpener: null, workspaces: workspaces, pathPicker: picker);

            Assert.False(vm.HasCustomRootDirectory);
            Assert.False(vm.HasRootDirectoryError);
            Assert.Equal(workspaces.DefaultWorkspacesRootDirectory, vm.EffectiveRootDirectoryPath);

            // Relative path shows validation error and does not overwrite settings.
            vm.RootDirectoryInput = "relative/worktrees";
            vm.FlushPending();
            Assert.True(vm.HasRootDirectoryError);
            Assert.Null(settings.Current.Workspaces.RootDirectory);

            // Path pointing to an existing file is rejected.
            vm.RootDirectoryInput = existingFile;
            vm.FlushPending();
            Assert.True(vm.HasRootDirectoryError);
            Assert.Null(settings.Current.Workspaces.RootDirectory);

            // Valid custom absolute path persists and updates WorkspaceManager.
            vm.RootDirectoryInput = customDir;
            vm.FlushPending();
            Assert.False(vm.HasRootDirectoryError);
            Assert.True(vm.HasCustomRootDirectory);
            Assert.Equal(customDir, settings.Current.Workspaces.RootDirectory);
            Assert.Equal(customDir, workspaces.WorkspacesRootDirectory);
            Assert.Equal(customDir, vm.EffectiveRootDirectoryPath);

            // Browse picks a folder and saves immediately.
            await vm.BrowseRootDirectoryCommand.ExecuteAsync(null);
            Assert.Equal(pickedDir, vm.RootDirectoryInput);
            Assert.Equal(pickedDir, settings.Current.Workspaces.RootDirectory);
            Assert.Equal(pickedDir, workspaces.WorkspacesRootDirectory);

            // Reset restores default directory.
            vm.ResetRootDirectoryCommand.Execute(null);
            Assert.Equal("", vm.RootDirectoryInput);
            Assert.Null(settings.Current.Workspaces.RootDirectory);
            Assert.Equal(workspaces.DefaultWorkspacesRootDirectory, workspaces.WorkspacesRootDirectory);

            // Alias "workspaces" opens the same settings section.
            var aliasView = new SettingsView { InitialSection = "workspaces" };
            var aliasWin = new Window { Width = 1100, Height = 820, Content = aliasView };
            aliasWin.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                var settingsVm = Assert.IsType<SettingsViewModel>(aliasView.DataContext);
                Assert.Equal("workspace-cleanup", settingsVm.Selected.Key);
                Assert.IsType<WorkspaceCleanupSettingsViewModel>(settingsVm.Page);
            }
            finally
            {
                aliasWin.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch (IOException) { }
        }
    }

    [AvaloniaFact, FreshApp]
    public async Task WorkspaceCleanupSettings_and_InspectWorkspacesView_persist_options_and_render_cleanly()
    {
        var settings = AppHost.Get<ISettingsStore>();
        var cleanup = AppHost.Get<IWorkspaceCleanupService>();
        var projects = AppHost.Get<IProjectService>();
        var dialogs = AppHost.Get<FakeDialogService>();

        using var vm = new WorkspaceCleanupSettingsViewModel(settings, cleanup, projects, dialogs);
        Assert.False(vm.IsAutoEnabled);
        Assert.False(vm.DeleteMerged);
        Assert.False(vm.DeleteUnchanged);
        Assert.False(vm.DeleteInactive);
        Assert.Equal(30, vm.InactiveDays);
        Assert.True(vm.DeleteMergedBranch);

        vm.DeleteMerged = true;
        Assert.True(vm.IsAutoEnabled);
        Assert.True(settings.Current.WorkspaceCleanup.DeleteMerged);

        vm.InactiveDays = 14;
        vm.CheckIntervalKey = "360";
        vm.UnchangedGraceKey = "6";
        vm.DeleteMergedBranch = false;
        vm.FetchBeforeCheck = true;

        Assert.Equal(14, settings.Current.WorkspaceCleanup.InactiveDays);
        Assert.Equal(360, settings.Current.WorkspaceCleanup.CheckIntervalMinutes);
        Assert.Equal(6, settings.Current.WorkspaceCleanup.UnchangedGraceHours);
        Assert.False(settings.Current.WorkspaceCleanup.DeleteMergedBranch);
        Assert.True(settings.Current.WorkspaceCleanup.FetchBeforeCheck);

        // Render settings page (with Advanced expanded) + inspect dialog (empty and populated) in headless Avalonia to verify all compiled bindings & layout.
        var settingsView = new SettingsView { InitialSection = "workspace-cleanup" };
        var win = new Window { Width = 1100, Height = 820, Content = settingsView };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var settingsVm = Assert.IsType<SettingsViewModel>(settingsView.DataContext);
            Assert.Equal("workspace-cleanup", settingsVm.Selected.Key);
            var pageVm = Assert.IsType<WorkspaceCleanupSettingsViewModel>(settingsVm.Page);
            pageVm.IsAdvancedExpanded = true;
            Frames.Pump();
            win.UpdateLayout();
        }
        finally
        {
            win.Close();
            Dispatcher.UIThread.RunJobs();
        }

        if (FindGit() is not null)
        {
            var (root, repo, _) = CreateGitRepoWithRemote("InspectUiDemo");
            try
            {
                var manager = AppHost.Get<IWorkspaceManager>();
                var project = projects.AddProject(repo, "InspectUiDemo");
                var wsClean = (await manager.CreateAsync(project, new CreateWorkspaceRequest(true, "feat/ui-clean", "main", false, null, false, WorkspaceAfterCreate.None))).Workspace!;
                var wsPinned = (await manager.CreateAsync(project, new CreateWorkspaceRequest(true, "feat/ui-pinned", "main", false, null, false, WorkspaceAfterCreate.None))).Workspace!;
                projects.SetWorkspacePinned(project, wsPinned.Id, true);

                var inspectVm = new InspectWorkspacesViewModel(cleanup, projects, fileOpener: null, deleteMergedBranch: true);
                await inspectVm.LoadAsync();
                Assert.Equal(2, inspectVm.Filtered.Count);
                Assert.Equal(1, inspectVm.SelectedCount);

                // Filter to Protected shows only pinned row.
                inspectVm.Filter = "protected";
                var protectedRow = Assert.Single(inspectVm.Filtered);
                Assert.Equal(wsPinned.Id, protectedRow.Candidate.Workspace.Id);
                Assert.False(protectedRow.CanSelect);

                // Render populated InspectWorkspacesView to verify all row DataTemplate compiled bindings.
                inspectVm.Filter = "all";
                var inspectWin = new Window { Width = 740, Height = 560, Content = new InspectWorkspacesView(inspectVm) };
                inspectWin.Show();
                Dispatcher.UIThread.RunJobs();
                try
                {
                    Frames.Pump();
                    inspectWin.UpdateLayout();
                    Assert.True(inspectWin.Bounds.Height > 0);
                }
                finally
                {
                    inspectWin.Close();
                    Dispatcher.UIThread.RunJobs();
                }

                // Unpin from dialog refreshes row state and enables selection.
                await protectedRow.TogglePinCommand.ExecuteAsync(null);
                Assert.False(wsPinned.IsPinned);
                Assert.True(inspectVm.Filtered.Single(r => r.Candidate.Workspace.Id == wsPinned.Id).CanSelect);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }
    }
}
