using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.App.Composition;
using Octopus.App.Explorer;
using Octopus.App.Explorer.Files;
using Octopus.App.Files;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.App.Tests.Explorer;
using Octopus.App.Workspace;
using Octopus.App.Tests;
using Octopus.FileSystem;
using Octopus.Git;
using Octopus.Core.Projects;
using Octopus.Storage;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests.Files;

public class DiffTabTests
{
    private static DiffTabViewModel Open(ExplorerFixture repo, string file, bool staged) =>
        new(new DiffTabRequest(repo.Root, file, Path.Combine(repo.Root, file), staged),
            AppHost.Get<IGitService>(), AppHost.Get<IFileTreeService>(), AppHost.Get<ISettingsStore>());

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition(), "the condition did not become true in time");
    }

    private static ExplorerFixture Repository()
    {
        var repo = new ExplorerFixture();
        repo.InitRepository();
        repo.Write("a.txt", "one\ntwo\nthree\n");
        repo.Write("pic.bin", "x");
        repo.Git("add", ".");
        repo.Git("commit", "-q", "-m", "first");
        return repo;
    }

    [AvaloniaFact]
    public async Task A_diff_tab_shows_numbered_lines_and_follows_the_file_and_the_index()
    {
        if (ExplorerFixture.FindGit() is null) return;
        using var repo = Repository();
        repo.Write("a.txt", "one\nTWO\nthree\n");
        using var worktree = Open(repo, "a.txt", staged: false);
        using var staged = Open(repo, "a.txt", staged: true);
        await WaitUntil(() => worktree.Rows.Count > 0);

        Assert.Equal(1, worktree.Added);
        Assert.Equal(1, worktree.Removed);
        Assert.Contains(worktree.Rows, r => r.IsAdded && r.Text == "TWO" && r.NewNumber.Trim() == "2");
        Assert.Contains(worktree.Rows, r => r.IsRemoved && r.Text == "two" && r.OldNumber.Trim() == "2");
        Assert.Single(worktree.Rows, r => r.IsHunk);
        Assert.True(worktree.HasRows);
        await WaitUntil(() => staged.Notice is not null);
        Assert.False(staged.HasRows);                  // nothing staged yet

        // The file is saved again: the open tab reads it again by itself.
        repo.Write("a.txt", "one\nTWO\nthree\nfour\n");
        await WaitUntil(() => worktree.Added == 2);

        // `git add` in a terminal moves the change from one tab to the other.
        repo.Git("add", "a.txt");
        await WaitUntil(() => staged.Added == 2);
        await WaitUntil(() => worktree.Notice is not null);
    }

    [AvaloniaFact]
    public async Task A_binary_file_and_a_huge_diff_show_a_note_instead_of_lines()
    {
        if (ExplorerFixture.FindGit() is null) return;
        using var repo = Repository();
        File.WriteAllBytes(Path.Combine(repo.Root, "pic.bin"), [0, 1, 2, 0, 0]);
        repo.Write("many.txt", string.Concat(Enumerable.Range(0, GitDiffParser.MaxLines + 500).Select(i => $"line {i}\n")));
        using var binary = Open(repo, "pic.bin", staged: false);
        using var many = Open(repo, "many.txt", staged: false);
        await WaitUntil(() => binary.Notice is not null && many.Rows.Count > 0);

        Assert.False(binary.HasRows);
        Assert.True(binary.HasNotice);
        Assert.Equal(GitDiffParser.MaxLines, many.Rows.Count);
        Assert.True(many.HasTruncatedNotice);
        Assert.Contains((GitDiffParser.MaxLines + 501).ToString(), many.TruncatedNotice);
    }

    [AvaloniaFact]
    public async Task Asking_for_the_same_diff_again_activates_the_open_tab_and_it_is_never_a_project_item()
    {
        if (ExplorerFixture.FindGit() is null) return;
        using var repo = Repository();
        repo.Write("a.txt", "one\nTWO\nthree\n");
        var projects = AppHost.Get<IProjectService>();
        var project = projects.AddProject(repo.Root);
        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var items = project.Items.Count;
        var commands = AppHost.Get<IWorkspaceCommands>();
        var layout = (DockLayout)AppHost.Get<WorkspaceViewModel>().CurrentLayout!;
        var request = new DiffTabRequest(repo.Root, "a.txt", Path.Combine(repo.Root, "a.txt"), false);

        commands.OpenDiff(request);
        Dispatcher.UIThread.RunJobs();
        var pane = layout.Workspace.FindPane(request.Key);
        Assert.NotNull(pane);
        Assert.Same(pane, layout.ActivePane);
        Assert.Equal(Octopus.App.Localization.AppLangResources.Format(Octopus.App.Localization.AppLangResourceKind.DiffTitleChanges, "a.txt"), pane!.Title);
        Assert.Equal(items, project.Items.Count);        // not persisted as an item

        // The person looks at another tab, then clicks the file again: same tab, activated.
        var terminal = AppHost.Get<IItemOpener>().OpenTerminal()!;
        Dispatcher.UIThread.RunJobs();
        Assert.NotSame(pane, layout.ActivePane);
        commands.OpenDiff(request);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(pane, layout.ActivePane);
        Assert.Single(layout.Workspace.AllPanes(), p => p.Id == request.Key);

        // The staged side of the same file is another tab.
        commands.OpenDiff(request with { Staged = true });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, layout.Workspace.AllPanes().Count(p => p.Id.StartsWith("diff:", StringComparison.Ordinal)));

        // A restart: the saved layout still names the diff panes, but nothing resolves them, so they are dropped.
        var json = layout.SaveLayout();
        var restored = new DockLayout
        {
            Workspace = new DockWorkspace { Root = new DockDocumentArea() },
            PaneResolver = id => project.Items.FirstOrDefault(i => i.Id == id) is { } item ? new DockPane { Id = item.Id, Title = item.DisplayTitle } : null,
        };
        restored.LoadLayout(json);
        Assert.DoesNotContain(restored.Workspace.AllPanes(), p => p.Id.StartsWith("diff:", StringComparison.Ordinal));
        Assert.Contains(restored.Workspace.AllPanes(), p => p.Id == terminal.Id);

        // Closing the tab ends it and lets go of its watchers.
        layout.Close(pane);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(AppHost.Get<ITabContentRegistry>().Find(request.Key));
        Assert.Equal(items + 1, project.Items.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Clicking_a_change_in_the_git_tab_asks_for_its_diff_and_a_folder_has_none()
    {
        if (ExplorerFixture.FindGit() is null) return;
        using var repo = Repository();
        repo.Write("a.txt", "one\nTWO\nthree\n");
        repo.Write("dir/new.txt", "x\n");
        var panel = AppHost.Get<ExplorerPanelViewModel>();
        await panel.SetProjectAsync(repo.Root, "Repo");
        var requests = new List<DiffTabRequest>();
        panel.DiffRequested += (_, r) => requests.Add(r);

        panel.Git.RequestDiff(panel.Git.Groups.SelectMany(g => g.Rows).Single(r => r.Name == "a.txt"));
        panel.Git.RequestDiff(panel.Git.Groups.SelectMany(g => g.Rows).Single(r => r.Name == "new.txt"));
        panel.Git.RequestDiff(null);

        Assert.Equal(2, requests.Count);
        Assert.False(requests[0].Staged);
        Assert.Equal("a.txt", requests[0].Path);
        Assert.Equal("dir/new.txt", requests[1].Path);
        panel.Dispose();
    }

    [AvaloniaFact]
    public async Task A_file_tab_reads_again_when_the_file_changes_and_explains_binary_and_large_files()
    {
        using var dir = new ExplorerFixture();
        var text = dir.Write("a.txt", "one\n");
        var binary = dir.Write("b.bin", "x\0y");
        var big = dir.Write("big.txt", new string('x', (int)Octopus.App.Files.Code.CodeViewerModel.MaxBytes + 1));
        FileTabViewModel Tab(string path) => new(new ProjectItem { Kind = ProjectItemKind.File, FilePath = path },
            AppHost.Get<IFileTreeService>(), AppHost.Get<ISettingsStore>());
        using var tab = Tab(text);
        using var binaryTab = Tab(binary);
        using var bigTab = Tab(big);
        static Octopus.App.Files.Code.CodeViewerModel Code(FileTabViewModel t) => (Octopus.App.Files.Code.CodeViewerModel)t.Viewer;
        await WaitUntil(() => Code(tab).Text == "one\n" && Code(binaryTab).HasNotice && Code(bigTab).HasNotice);
        Assert.True(Code(tab).HasContent);

        File.WriteAllText(text, "one\ntwo\n");
        await WaitUntil(() => Code(tab).Text == "one\ntwo\n");

        File.Delete(text);
        await WaitUntil(() => Code(tab).HasNotice);
        Assert.Equal("", Code(tab).Text);
        File.WriteAllText(text, "back\n");
        await WaitUntil(() => Code(tab).Text == "back\n" && Code(tab).HasContent);
    }

    [AvaloniaFact]
    public async Task A_file_tab_keeps_its_scroll_position_when_the_file_changes_and_a_long_file_stays_usable()
    {
        using var dir = new ExplorerFixture();
        var lines = Enumerable.Range(1, 20_000).Select(i => $"line {i} of a long file").ToList();
        var path = dir.Write("long.txt", string.Join('\n', lines) + "\n");
        var model = new FileTabViewModel(new ProjectItem { Kind = ProjectItemKind.File, FilePath = path },
            AppHost.Get<IFileTreeService>(), AppHost.Get<ISettingsStore>());
        var view = new FileTabView { DataContext = model };
        var window = new Avalonia.Controls.Window { Width = 700, Height = 400, Content = view };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        window.Show();
        await WaitUntil(() => ((Octopus.App.Files.Code.CodeViewerModel)model.Viewer).Text.Length > 0);
        Frames.Pump();
        var scroller = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<ScrollArea>().First();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"showing 20000 lines took {clock.Elapsed}");

        scroller.Offset = new Avalonia.Vector(0, 3000);
        Frames.Pump();
        Assert.Equal(3000, scroller.Offset.Y, 1);
        lines[10] = "changed";
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        await WaitUntil(() => ((Octopus.App.Files.Code.CodeViewerModel)model.Viewer).Text.Contains("changed"));
        await Task.Delay(500);
        Frames.Pump();

        Assert.Equal(3000, scroller.Offset.Y, 1);
        window.Close();
        model.Dispose();
    }
}
