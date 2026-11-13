using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Explorer;
using Octopus.App.Explorer.Files;
using Octopus.App.Explorer.Git;
using Octopus.App.Explorer.Search;
using Octopus.App.Localization;
using Octopus.Git;
using Xunit;

namespace Octopus.App.Tests.Explorer;

public class ExplorerViewModelTests
{
    [AvaloniaFact]
    public async Task The_tree_reads_folders_one_step_ahead_so_opening_one_never_shows_an_empty_row()
    {
        using var project = new ExplorerFixture();
        project.Write("src/Program.cs", "class P {}");
        project.Write("src/deep/x.txt");
        project.Write("src/empty/.keep-not");
        project.Write("README.md");
        project.Write(".env");
        Directory.CreateDirectory(Path.Combine(project.Root, "void"));
        var tree = AppHost.Get<FileTreeViewModel>();

        tree.Load(project.Root);
        await tree.ReadAheadIdle;

        Assert.Equal(["src", "void", ".env", "README.md"], tree.Roots.Select(n => n.Name).ToArray());
        var src = tree.Roots[0];
        Assert.True(src.IsDirectory);
        // The folders of the first level are already read: they have their real children, and no stand-in rows.
        Assert.True(src.IsLoaded);
        Assert.Equal(["deep", "empty", "Program.cs"], src.Children.Select(n => n.Name).ToArray());
        Assert.Empty(tree.Roots[1].Children);                 // an empty folder has nothing to open
        Assert.True(tree.Roots[2].IsHidden);
        // The next level is read when its parent opens.
        var deep = src.Children[0];
        Assert.False(deep.IsLoaded);
        Assert.Empty(deep.Children);

        tree.EnsureLoaded(src);
        await tree.ReadAheadIdle;

        Assert.True(deep.IsLoaded);
        Assert.Equal(["x.txt"], deep.Children.Select(n => n.Name).ToArray());
    }

    [AvaloniaFact]
    public async Task A_changed_directory_keeps_the_rows_that_remain()
    {
        using var project = new ExplorerFixture();
        project.Write("src/a.cs");
        project.Write("src/b.cs");
        var tree = AppHost.Get<FileTreeViewModel>();
        tree.Load(project.Root);
        await tree.ReadAheadIdle;
        var src = tree.Roots[0];
        tree.EnsureLoaded(src);
        var a = src.Children[0];
        tree.SelectedNode = a;

        project.Write("src/0.cs");
        File.Delete(Path.Combine(project.Root, "src", "b.cs"));
        tree.RefreshDirectory(src.FullPath);

        Assert.Equal(["0.cs", "a.cs"], src.Children.Select(n => n.Name).ToArray());
        Assert.Same(a, src.Children[1]);
        Assert.Same(a, tree.SelectedNode);
    }

    [AvaloniaFact]
    public void Hidden_files_can_be_left_out()
    {
        using var project = new ExplorerFixture();
        project.Write(".env");
        project.Write("a.txt");
        var tree = AppHost.Get<FileTreeViewModel>();
        tree.Load(project.Root);
        Assert.Equal(2, tree.Roots.Count);

        tree.ShowHidden = false;

        Assert.Equal(["a.txt"], tree.Roots.Select(n => n.Name).ToArray());
    }

    [AvaloniaFact]
    public async Task Search_finds_files_by_name_and_by_content()
    {
        using var project = new ExplorerFixture();
        project.Write("src/Widget.cs", "class Widget\n{\n    // find the Needle here\n}\n");
        project.Write("docs/widget-notes.md", "nothing\n");
        project.Write("node_modules/Widget.js", "Needle");
        var search = AppHost.Get<FileSearchViewModel>();
        search.SetRoot(project.Root);

        search.Query = "widget";
        await search.SearchNowAsync();
        Assert.True(search.IsActive);
        Assert.Equal(["Widget.cs", "widget-notes.md"], search.Results.Select(r => r.Title).ToArray());
        Assert.Equal("src", search.Results[0].Subtitle);

        search.ModeKey = "content";
        search.Query = "needle";
        await search.SearchNowAsync();
        Assert.Equal(2, search.Results.Count);
        Assert.True(search.Results[0].IsHeader);
        var match = search.Results[1];
        Assert.Equal(3, match.Line);
        Assert.Equal("// find the Needle here", match.Title);
        Assert.Equal("Needle", match.Title.Substring(match.HighlightStart, match.HighlightLength));

        search.Query = "zzz-not-there";
        await search.SearchNowAsync();
        Assert.True(search.HasNoMatches);
    }

    [AvaloniaFact]
    public async Task A_folder_outside_git_has_no_git_tab()
    {
        using var project = new ExplorerFixture();
        project.Write("a.txt");
        var panel = AppHost.Get<ExplorerPanelViewModel>();

        await panel.SetProjectAsync(project.Root, "Plain");
        panel.ShowGitCommand.Execute(null);

        Assert.True(panel.HasProject);
        Assert.False(panel.IsGitRepository);
        Assert.True(panel.IsFilesTab);
        Assert.False(panel.IsGitTab);
        Assert.Equal("Plain", panel.ProjectName);
        panel.Dispose();
    }

    [AvaloniaFact]
    public async Task No_project_shows_the_empty_state()
    {
        var panel = AppHost.Get<ExplorerPanelViewModel>();
        await panel.SetProjectAsync(null, null);
        Assert.True(panel.HasNoProject);
        Assert.False(panel.IsFilesTab);
        Assert.Empty(panel.Files.Roots);
        panel.Dispose();
    }

    [AvaloniaFact]
    public async Task A_repository_has_a_git_tab_with_grouped_changes()
    {
        if (ExplorerFixture.FindGit() is null) return;
        using var project = new ExplorerFixture();
        project.InitRepository();
        project.Write("a.txt", "one\n");
        project.Write("b.txt", "bee\n");
        project.Write("c.txt", "see\n");
        project.Git("add", ".");
        project.Git("commit", "-q", "-m", "first");
        project.Write("a.txt", "one\ntwo\n");           // changed
        project.Write("b.txt", "bee\nstaged\n");        // staged
        project.Git("add", "b.txt");
        File.Delete(Path.Combine(project.Root, "c.txt")); // deleted
        project.Write("new/file.txt", "x");             // untracked

        var panel = AppHost.Get<ExplorerPanelViewModel>();
        await panel.SetProjectAsync(project.Root, "Repo");

        Assert.True(panel.IsGitRepository);
        Assert.Equal(4, panel.ChangeCount);
        Assert.True(panel.HasChanges);
        Assert.NotEmpty(panel.Git.BranchName);
        Assert.Equal(
            [AppLangResources.Get(AppLangResourceKind.ExplorerStagedChanges), AppLangResources.Get(AppLangResourceKind.ExplorerChanges), AppLangResources.Get(AppLangResourceKind.ExplorerUntracked)],
            panel.Git.Groups.Select(g => g.Title).ToArray());
        Assert.Equal(["b.txt"], panel.Git.Groups[0].Rows.Select(r => r.Name).ToArray());
        Assert.Equal(["a.txt", "c.txt"], panel.Git.Groups[1].Rows.Select(r => r.Name).ToArray());
        Assert.Equal(["file.txt"], panel.Git.Groups[2].Rows.Select(r => r.Name).ToArray());
        Assert.Equal("new", panel.Git.Groups[2].Rows[0].Directory);
        // The commits are read when the Git tab opens.
        panel.ShowGitCommand.Execute(null);
        await panel.Git.RefreshAsync();
        Assert.True(panel.IsGitTab);
        Assert.Single(panel.Git.Commits);
        Assert.Equal("first", panel.Git.Commits[0].Subject);

        // The tree is decorated: the changed file has its letter and the folder with an untracked file a dot.
        var a = panel.Files.Roots.Single(n => n.Name == "a.txt");
        Assert.Equal(GitChangeKind.Modified, a.GitKind);
        Assert.True(panel.Files.Roots.Single(n => n.Name == "new").HasChangedDescendant);

        // A deleted file does not open; a changed one does.
        var opened = new List<string>();
        panel.FileOpenRequested += (_, path) => opened.Add(path);
        panel.Git.RequestOpen(panel.Git.Groups[1].Rows.Single(r => r.Name == "c.txt"));
        panel.Git.RequestOpen(panel.Git.Groups[1].Rows.Single(r => r.Name == "a.txt"));
        Assert.Single(opened);
        Assert.EndsWith("a.txt", opened[0]);
        panel.Dispose();
    }

    [AvaloniaFact]
    public async Task Editing_a_file_or_running_git_elsewhere_updates_the_git_tab()
    {
        if (ExplorerFixture.FindGit() is null) return;
        using var project = new ExplorerFixture();
        project.InitRepository();
        project.Write("a.txt", "one\n");
        project.Git("add", ".");
        project.Git("commit", "-q", "-m", "first");
        var panel = AppHost.Get<ExplorerPanelViewModel>();
        panel.GitRefreshDelay = TimeSpan.FromMilliseconds(100);
        await panel.SetProjectAsync(project.Root, "Repo");
        panel.ShowGitCommand.Execute(null);
        await WaitUntil(() => panel.Git.Commits.Count == 1);
        Assert.Equal(0, panel.ChangeCount);

        // A file that is already tracked is saved: the change shows up without any other event.
        project.Write("a.txt", "one\ntwo\n");
        await WaitUntil(() => panel.ChangeCount == 1);
        Assert.Equal(GitChangeKind.Modified, panel.Files.Roots.Single(n => n.Name == "a.txt").GitKind);

        // `git add` in a terminal only touches .git/index.
        project.Git("add", "a.txt");
        await WaitUntil(() => panel.Git.Groups.Count == 1 && panel.Git.Groups[0].Title == AppLangResources.Get(AppLangResourceKind.ExplorerStagedChanges));

        // `git commit` moves HEAD's branch and empties the list; the log follows because the Git tab is open.
        project.Git("commit", "-q", "-m", "second");
        await WaitUntil(() => panel.ChangeCount == 0 && panel.Git.Commits.Count == 2);
        Assert.True(panel.Git.IsClean);
        Assert.Null(panel.Files.Roots.Single(n => n.Name == "a.txt").GitKind);
        panel.Dispose();
    }

    [AvaloniaFact]
    public async Task A_project_below_the_repository_root_follows_git_run_above_it()
    {
        if (ExplorerFixture.FindGit() is null) return;
        using var repo = new ExplorerFixture();
        repo.InitRepository();
        repo.Write("top.txt", "one\n");
        repo.Write("apps/web/a.txt", "one\n");
        repo.Git("add", ".");
        repo.Git("commit", "-q", "-m", "first");
        var panel = AppHost.Get<ExplorerPanelViewModel>();
        panel.GitRefreshDelay = TimeSpan.FromMilliseconds(100);
        // The project is a sub-folder: the repository's .git is two levels above what the tree watcher sees.
        await panel.SetProjectAsync(Path.Combine(repo.Root, "apps", "web"), "Web");
        panel.ShowGitCommand.Execute(null);
        await WaitUntil(() => panel.Git.Commits.Count == 1);
        Assert.True(panel.IsGitRepository);
        await Task.Delay(1000);   // the platform may still replay the events of the setup

        // Stage a file outside the project folder: only .git/index moves, and nothing inside the project.
        repo.Write("top.txt", "one\ntwo\n");
        repo.Git("add", "top.txt");
        await WaitUntil(() => panel.Git.Groups.Count == 1 && panel.Git.Groups[0].Rows.Any(r => r.Name == "top.txt" && r.Change.IsStaged));

        // `git commit` and `git checkout -b` from a terminal: HEAD, the branch ref and the log follow.
        repo.Git("commit", "-q", "-m", "second");
        await WaitUntil(() => panel.ChangeCount == 0 && panel.Git.Commits.Count == 2);
        repo.Git("checkout", "-q", "-b", "feature");
        await WaitUntil(() => panel.Git.BranchName == "feature");
        panel.Dispose();
    }

    [AvaloniaFact]
    public void Many_untracked_files_are_not_marked_one_by_one_in_the_tree()
    {
        using var project = new ExplorerFixture();
        for (var i = 0; i < FileTreeViewModel.UntrackedMarkLimit + 1; i++) project.Write($"u/f{i}.txt");
        project.Write("m.txt");
        var tree = AppHost.Get<FileTreeViewModel>();
        tree.Load(project.Root);
        var root = RealPath.Resolve(project.Root);
        GitChange Untracked(string path) => new(path, Path.Combine(root, path), GitChangeKind.Untracked, false);
        var changes = Enumerable.Range(0, FileTreeViewModel.UntrackedMarkLimit + 1).Select(i => Untracked($"u/f{i}.txt")).ToList();
        changes.Add(new GitChange("m.txt", Path.Combine(root, "m.txt"), GitChangeKind.Modified, false));

        tree.ApplyGitStatus(new GitStatus("main", null, null, 0, 0, changes), root);

        var u = tree.Roots.Single(n => n.Name == "u");
        Assert.False(u.HasChangedDescendant);
        Assert.Equal(GitChangeKind.Modified, tree.Roots.Single(n => n.Name == "m.txt").GitKind);

        // At the limit they are marked.
        tree.ApplyGitStatus(new GitStatus("main", null, null, 0, 0, changes.Take(FileTreeViewModel.UntrackedMarkLimit).ToList()), root);
        Assert.True(u.HasChangedDescendant);
    }

    [AvaloniaFact]
    public void An_untracked_folder_listed_whole_is_marked_and_cannot_be_opened()
    {
        using var project = new ExplorerFixture();
        project.Write("bulk/a.txt");
        var tree = AppHost.Get<FileTreeViewModel>();
        tree.Load(project.Root);
        var root = RealPath.Resolve(project.Root);
        var folder = new GitChange("bulk", Path.Combine(root, "bulk"), GitChangeKind.Untracked, false, IsDirectory: true);

        tree.ApplyGitStatus(new GitStatus("main", null, null, 0, 0, [folder]), root);
        Assert.True(tree.Roots.Single().HasChangedDescendant);

        var git = AppHost.Get<GitPanelViewModel>();
        var opened = 0;
        git.OpenRequested += (_, _) => opened++;
        var row = new GitChangeRowViewModel(folder);
        git.RequestOpen(row);
        Assert.Equal("bulk/", row.Name);
        Assert.Equal(0, opened);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition(), "the condition did not become true in time");
    }
}
