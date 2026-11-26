using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Composition;
using Octopus.App.Explorer;
using Octopus.App.Explorer.Git;
using Octopus.App.Localization;
using Xunit;

namespace Octopus.App.Tests.Explorer;

/// <summary>The Git tab stages, unstages and commits, and its list stays cheap with thousands of changes.</summary>
public class GitCommitTests
{
    private static ExplorerFixture Repository()
    {
        var project = new ExplorerFixture();
        project.InitRepository();
        // The person's own signing or hooks must not reach the test's commits.
        project.Git("config", "commit.gpgsign", "false");
        project.Git("config", "core.hooksPath", "/dev/null");
        return project;
    }

    [AvaloniaFact]
    public async Task Changes_are_staged_unstaged_and_committed_from_the_git_tab()
    {
        if (ExplorerFixture.FindGit() is null) return;
        using var project = Repository();
        project.Write("a.txt", "one\n");
        project.Git("add", ".");
        project.Git("commit", "-q", "-m", "first");
        project.Write("a.txt", "one\ntwo\n");
        project.Write("b.txt", "bee\n");
        project.Write("c.txt", "see\n");

        var panel = AppHost.Get<ExplorerPanelViewModel>();
        await panel.SetProjectAsync(project.Root, "Repo");
        var git = panel.Git;
        await git.RefreshAsync();
        Assert.False(git.HasStaged);
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.ExplorerStageAllAndCommit), git.CommitButtonText);
        Assert.False(git.CommitCommand.CanExecute(null));   // no message yet

        // One file, then a whole group.
        await git.ToggleStageCommand.ExecuteAsync(git.Groups.Single(g => !g.IsStaged && g.Rows.Any(r => r.Name == "a.txt")).Rows[0]);
        Assert.Equal(["a.txt"], git.Groups.Single(g => g.IsStaged).Rows.Select(r => r.Name).ToArray());
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.ExplorerCommit), git.CommitButtonText);
        await git.StageGroupCommand.ExecuteAsync(git.Groups.Single(g => g.Title == AppLangResources.Get(AppLangResourceKind.ExplorerUntracked)));
        Assert.Equal(["a.txt", "b.txt", "c.txt"], git.Groups.Single().Rows.Select(r => r.Name).ToArray());

        // And back out.
        await git.ToggleStageCommand.ExecuteAsync(git.Groups.Single().Rows.Single(r => r.Name == "c.txt"));
        Assert.Equal(["a.txt", "b.txt"], git.Groups.Single(g => g.IsStaged).Rows.Select(r => r.Name).ToArray());

        // Commit only what is staged; the message is cleared and the new commit is listed.
        git.CommitMessage = "Add b";
        Assert.True(git.CommitCommand.CanExecute(null));
        await git.CommitCommand.ExecuteAsync(null);
        Assert.Null(git.GitError);
        Assert.Equal("", git.CommitMessage);
        Assert.Equal("Add b", git.Commits[0].Subject);
        Assert.Equal(["c.txt"], git.Groups.Single().Rows.Select(r => r.Name).ToArray());

        // Nothing staged: everything is staged and committed.
        git.CommitMessage = "Add c";
        await git.CommitCommand.ExecuteAsync(null);
        Assert.Equal("Add c", git.Commits[0].Subject);
        Assert.True(git.IsClean);
        Assert.Empty(git.Groups);
        Assert.False(git.CommitCommand.CanExecute(null));
        panel.Dispose();
    }

    [AvaloniaFact]
    public async Task A_failing_commit_shows_what_git_said_and_keeps_the_message()
    {
        if (ExplorerFixture.FindGit() is null || OperatingSystem.IsWindows()) return;
        using var project = Repository();
        project.Write("a.txt", "one\n");
        // A hook that refuses every commit.
        var hooks = Path.Combine(project.Root, ".hooks");
        Directory.CreateDirectory(hooks);
        var hook = Path.Combine(hooks, "pre-commit");
        File.WriteAllText(hook, "#!/bin/sh\necho 'lint failed: a.txt' >&2\nexit 1\n");
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        project.Git("config", "core.hooksPath", hooks);

        var panel = AppHost.Get<ExplorerPanelViewModel>();
        await panel.SetProjectAsync(project.Root, "Repo");
        var git = panel.Git;
        await git.RefreshAsync();
        git.CommitMessage = "Try";
        await git.CommitCommand.ExecuteAsync(null);

        Assert.True(git.HasGitError);
        Assert.Contains("lint failed: a.txt", git.GitError);
        Assert.Equal("Try", git.CommitMessage);
        Assert.Empty(git.Commits);
        panel.Dispose();
    }

    [AvaloniaFact]
    public async Task Thousands_of_changes_make_only_the_visible_rows()
    {
        if (ExplorerFixture.FindGit() is null) return;
        using var project = Repository();
        for (var i = 0; i < 3000; i++) project.Write($"gen/file{i:D4}.txt", "x");

        var model = new GitPanelViewModel(AppHost.Get<Octopus.Git.IGitService>());
        model.SetRepository(project.Root);
        await model.RefreshAsync();
        Assert.Equal(3000, model.Groups.Single().Rows.Count);
        Assert.Equal(3000 + 3, model.Items.Count);   // changes section heading, group heading, rows, commits heading

        var view = new GitTabView { DataContext = model };
        var window = new Window { Width = 320, Height = 760, Content = view };
        window.Show();
        Frames.Pump();
        var rows = window.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("ex-row"));
        Assert.InRange(rows, 1, 100);

        // The same status again keeps the list (and so the scroll position and the realized rows).
        var items = model.Items;
        await model.RefreshAsync();
        Assert.Same(items, model.Items);

        // The commit box is there, and its button names what it will do.
        var button = window.GetVisualDescendants().OfType<TKCode.Arc.Button>().Single(b => b.Name == "CommitButton");
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.ExplorerStageAllAndCommit), button.Content);
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
    [AvaloniaFact]
    public async Task Changes_are_discarded_after_asking_and_untracked_files_deleted()
    {
        if (ExplorerFixture.FindGit() is null) return;
        using var project = Repository();
        project.Write("a.txt", "one\n");
        project.Git("add", ".");
        project.Git("commit", "-q", "-m", "first");
        project.Write("a.txt", "changed\n");
        project.Write("b.txt", "new\n");
        var dialogs = AppHost.Get<FakeDialogService>();
        var panel = AppHost.Get<ExplorerPanelViewModel>();
        await panel.SetProjectAsync(project.Root, "Repo");
        var git = panel.Git;
        await git.RefreshAsync();

        // Said no: nothing happens.
        dialogs.Answer = false;
        await git.DiscardCommand.ExecuteAsync(git.Groups.Single(g => g.Kind == GitChangeGroupKind.Changed).Rows[0]);
        Assert.Equal("changed\n", File.ReadAllText(Path.Combine(project.Root, "a.txt")));

        dialogs.Answer = true;
        await git.DiscardCommand.ExecuteAsync(git.Groups.Single(g => g.Kind == GitChangeGroupKind.Changed).Rows[0]);
        Assert.Equal("one\n", File.ReadAllText(Path.Combine(project.Root, "a.txt")));
        Assert.Contains("a.txt", dialogs.LastTitle);

        await git.DiscardGroupCommand.ExecuteAsync(git.Groups.Single(g => g.Kind == GitChangeGroupKind.Untracked));
        Assert.False(File.Exists(Path.Combine(project.Root, "b.txt")));
        Assert.True(git.IsClean);
        panel.Dispose();
    }

    [AvaloniaFact]
    public async Task Branches_are_created_switched_and_deleted_and_the_last_commit_amended()
    {
        if (ExplorerFixture.FindGit() is null) return;
        using var project = Repository();
        project.Write("a.txt", "one\n");
        project.Git("add", ".");
        project.Git("commit", "-q", "-m", "first");
        var main = project.GitOutput("branch", "--show-current");
        var dialogs = AppHost.Get<FakeDialogService>();
        dialogs.Answer = true;
        var panel = AppHost.Get<ExplorerPanelViewModel>();
        await panel.SetProjectAsync(project.Root, "Repo");
        var git = panel.Git;
        await git.RefreshAsync();
        Assert.Equal([main], git.LocalBranches.Select(b => b.Name).ToArray());

        dialogs.NameAnswer = "feature";
        await git.NewBranchCommand.ExecuteAsync(null);
        Assert.Equal("feature", git.BranchName);
        Assert.True(git.LocalBranches.Single(b => b.Name == "feature").IsCurrent);

        // A commit on the branch, then back: the branch is not merged, so deleting asks twice.
        project.Write("b.txt", "b\n");
        git.CommitMessage = "on feature";
        await git.CommitCommand.ExecuteAsync(null);
        await git.SwitchBranchCommand.ExecuteAsync(git.LocalBranches.Single(b => b.Name == main));
        Assert.Equal(main, git.BranchName);
        var asked = dialogs.Asked;
        await git.DeleteBranchCommand.ExecuteAsync(git.LocalBranches.Single(b => b.Name == "feature"));
        Assert.Equal(asked + 2, dialogs.Asked);
        Assert.Null(git.GitError);
        Assert.Equal([main], git.LocalBranches.Select(b => b.Name).ToArray());

        // Amend: the message box starts from the last message; the staged change joins the commit.
        project.Write("c.txt", "c\n");
        await git.RefreshAsync();
        await git.StageGroupCommand.ExecuteAsync(git.Groups.Single());
        git.IsAmend = true;
        await FileTabWait(() => git.CommitMessage == "first");
        Assert.Equal(AppLangResources.Get(AppLangResourceKind.ExplorerAmend), git.CommitButtonText);
        git.CommitMessage = "first, amended";
        await git.CommitCommand.ExecuteAsync(null);
        Assert.False(git.IsAmend);
        Assert.Single(git.Commits);
        Assert.Equal("first, amended", git.Commits[0].Subject);
        Assert.True(git.IsClean);
        panel.Dispose();
    }

    [AvaloniaFact]
    public async Task A_commit_opens_to_its_files_and_a_file_shows_what_the_commit_did()
    {
        if (ExplorerFixture.FindGit() is null) return;
        using var project = Repository();
        project.Write("a.txt", "one\n");
        project.Git("add", ".");
        project.Git("commit", "-q", "-m", "first");
        project.Write("a.txt", "one\ntwo\n");
        project.Write("dir/b.txt", "b\n");
        project.Git("add", ".");
        project.Git("commit", "-q", "-m", "second");
        var panel = AppHost.Get<ExplorerPanelViewModel>();
        await panel.SetProjectAsync(project.Root, "Repo");
        var git = panel.Git;
        await git.RefreshAsync();
        var requests = new List<Octopus.App.Files.DiffTabRequest>();
        git.DiffRequested += (_, r) => requests.Add(r);

        var second = git.Commits[0];
        await git.ToggleCommit(second);
        Assert.True(second.IsExpanded);
        var files = git.Items.OfType<GitCommitFileRowViewModel>().ToList();
        Assert.Equal(["a.txt", "b.txt"], files.Select(f => f.Name).ToArray());
        Assert.Equal("dir", files[1].Directory);

        git.RequestCommitFileDiff(files[0]);
        var request = Assert.Single(requests);
        Assert.Equal(second.Commit.Hash, request.Commit);
        using var diff = new Octopus.App.Files.DiffTabViewModel(request, AppHost.Get<Octopus.Git.IGitService>(),
            AppHost.Get<Octopus.FileSystem.IFileTreeService>(), AppHost.Get<Octopus.Storage.ISettingsStore>());
        await diff.RefreshAsync();
        Assert.Equal(1, diff.Added);
        Assert.Contains(second.Commit.ShortHash[..7], diff.Title);

        // A refresh keeps it open; a second click closes it.
        await git.RefreshAsync();
        Assert.True(git.Commits[0].IsExpanded);
        await git.ToggleCommit(git.Commits[0]);
        Assert.Empty(git.Items.OfType<GitCommitFileRowViewModel>());
        panel.Dispose();
    }

    [AvaloniaFact]
    public async Task Sections_collapse_commits_paginate_and_branch_graph_mounts_only_when_expanded()
    {
        if (ExplorerFixture.FindGit() is null) return;
        using var project = Repository();
        var totalCommits = GitPanelViewModel.CommitPageSize + 6;
        for (var i = 1; i <= totalCommits; i++)
        {
            project.Write($"c{i:D2}.txt", $"v{i}\n");
            project.Git("add", ".");
            project.Git("commit", "-q", "-m", $"commit-{i:D2}");
        }
        var main = project.GitOutput("branch", "--show-current");
        project.Git("checkout", "-q", "-b", "feat/ui");
        project.Write("feat.txt", "feature\n");
        project.Git("add", ".");
        project.Git("commit", "-q", "-m", "feat-commit");
        project.Git("checkout", "-q", main);
        project.Git("merge", "-q", "--no-ff", "feat/ui", "-m", "Merge branch 'feat/ui'");
        project.Write("dirty.txt", "wip\n");

        var model = new GitPanelViewModel(AppHost.Get<Octopus.Git.IGitService>());
        model.SetRepository(project.Root);
        await model.RefreshAsync();

        // Commits load one page at a time instead of everything at once.
        Assert.Equal(GitPanelViewModel.CommitPageSize, model.Commits.Count);
        Assert.True(model.HasMoreCommits);
        Assert.Contains(model.Items, i => i is GitCommitsFooterViewModel);

        // Branch graph starts collapsed and does not load until expanded.
        Assert.False(model.IsBranchGraphExpanded);
        Assert.Empty(model.BranchGraphCommits);

        var view = new GitTabView { DataContext = model };
        var window = new Window { Width = 320, Height = 760, Content = view };
        window.Show();
        Frames.Pump();
        Assert.Empty(window.GetVisualDescendants().OfType<TKCode.Arc.BranchGraph>());

        // Collapsing Changes and Commits hides their rows.
        Assert.True(model.IsChangesExpanded);
        Assert.True(model.IsCommitsExpanded);
        model.ToggleChangesSectionCommand.Execute(null);
        Assert.False(model.IsChangesExpanded);
        Assert.Empty(model.Items.OfType<GitChangeRowViewModel>());
        model.ToggleCommitsSectionCommand.Execute(null);
        Assert.False(model.IsCommitsExpanded);
        Assert.Empty(model.Items.OfType<GitCommitRowViewModel>());
        model.ToggleCommitsSectionCommand.Execute(null);
        Assert.Equal(GitPanelViewModel.CommitPageSize, model.Items.OfType<GitCommitRowViewModel>().Count());

        // Loading the next page appends the remaining commits and clears HasMoreCommits.
        await model.LoadMoreCommitsAsync();
        Assert.Equal(totalCommits + 2, model.Commits.Count); // + feat-commit + merge commit
        Assert.False(model.HasMoreCommits);
        Assert.DoesNotContain(model.Items, i => i is GitCommitsFooterViewModel);

        // Expanding Branch Graph lazily loads commits and mounts the Arc BranchGraph control.
        await model.ToggleBranchGraphCommand.ExecuteAsync(null);
        Frames.Pump();
        Assert.True(model.IsBranchGraphExpanded);
        Assert.NotEmpty(model.BranchGraphCommits);
        Assert.Contains(model.BranchGraphCommits, c => c.Branch == "feat/ui");
        var graphControl = Assert.Single(window.GetVisualDescendants().OfType<TKCode.Arc.BranchGraph>());
        Assert.False(graphControl.AutoPlay);
        Assert.False(graphControl.AreControlsVisible);

        // Collapsing Branch Graph unmounts it again.
        await model.ToggleBranchGraphCommand.ExecuteAsync(null);
        Frames.Pump();
        Assert.False(model.IsBranchGraphExpanded);
        Assert.Empty(window.GetVisualDescendants().OfType<TKCode.Arc.BranchGraph>());

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task FileTabWait(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition());
    }
}
