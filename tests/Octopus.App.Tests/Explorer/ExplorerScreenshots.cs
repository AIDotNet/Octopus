using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Explorer;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests.Explorer;

/// <summary>
/// Renders the right panel (320 x 760) with Skia on the headless platform and saves PNGs to <c>tests/Octopus.App.Tests/bin/shots</c>
/// for a person to look at: the file tree, both searches and the Git tab, in light and dark.
/// </summary>
public class ExplorerScreenshots
{
    private static readonly string s_dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "shots"));

    [AvaloniaFact]
    public async Task Render_the_right_panel()
    {
        Directory.CreateDirectory(s_dir);
        using var repo = ExplorerFixture.FindGit() is null ? null : CreateRepository();
        var projectRoot = FindRepositoryRoot();
        foreach (var dark in new[] { false, true })
        {
            ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
            var suffix = dark ? "dark" : "light";

            // The project folder (not a git repository here): the tree with `src` open.
            var (window, view) = Open();
            view.ProjectName = "Octopus";
            view.ProjectPath = projectRoot;
            await WaitUntil(() => view.Model.IsFilesTab && view.Model.Files.Roots.Count > 0);
            view.Model.ShowFilesCommand.Execute(null);
            await Settle();
            var tree = window.GetVisualDescendants().OfType<TKCode.Arc.TreeView>().First();
            var src = view.Model.Files.Roots.First(n => n.Name == "src");
            tree.Expand(src);
            await Settle();
            view.Model.Files.SelectedNode = src.Children.FirstOrDefault(n => n.Name == "Octopus.App");
            tree.Expand(src.Children.First(n => n.Name == "Octopus.App"));
            await Settle();
            Save(window, "files", suffix);

            view.Model.Search.Query = "Explorer";
            await view.Model.Search.SearchNowAsync();
            await Settle();
            Save(window, "search-name", suffix);

            view.Model.Search.ModeKey = "content";
            view.Model.Search.Query = "FileOpenRequested";
            await view.Model.Search.SearchNowAsync();
            await Settle();
            Save(window, "search-content", suffix);

            view.Model.Search.Query = "";
            view.Model.Search.ModeKey = "name";
            window.Close();

            // A git repository with every kind of change: the Git tab, and the badge and letters in the tree.
            if (repo is not null)
            {
                (window, view) = Open();
                view.ProjectName = "Atlas";
                view.ProjectPath = repo.Root;
                await WaitUntil(() => view.Model.IsGitRepository && view.Model.ChangeCount > 0);
                view.Model.ShowFilesCommand.Execute(null);
                var atlasTree = window.GetVisualDescendants().OfType<TKCode.Arc.TreeView>().First();
                atlasTree.Expand(view.Model.Files.Roots.First(n => n.Name == "src"));
                await Settle();
                atlasTree.Expand(view.Model.Files.Roots.First(n => n.Name == "src").Children.First(n => n.Name == "Atlas.Core"));
                await Settle();
                Save(window, "files-git", suffix);
                view.Model.ShowGitCommand.Execute(null);
                await WaitUntil(() => view.Model.Git.Commits.Count > 0);
                await Settle();
                Save(window, "git", suffix);
                window.Close();
            }

            (window, view) = Open();
            Save(window, "empty", suffix);
            window.Close();
        }
    }

    private static ExplorerFixture CreateRepository()
    {
        var repo = new ExplorerFixture();
        repo.InitRepository();
        repo.Write("README.md", "# Atlas\n");
        repo.Write("src/Program.cs", "class Program {}\n");
        repo.Write("src/Atlas.Core/Map.cs", "class Map {}\n");
        repo.Write("src/Atlas.Core/Layer.cs", "class Layer {}\n");
        repo.Write("docs/guide.md", "guide\n");
        repo.Write("old.txt", "old\n");
        repo.Write("package.json", "{}\n");
        repo.Git("add", ".");
        repo.GitAt("2026-11-01T10:00:00", "commit", "-q", "-m", "Initial import of the map engine");
        repo.Write("src/Atlas.Core/Map.cs", "class Map { int Zoom; }\n");
        repo.Git("add", ".");
        repo.GitAt(DateTimeOffset.Now.AddDays(-3).ToString("o"), "commit", "-q", "-m", "Add zoom levels to Map");
        repo.Write("docs/guide.md", "guide\nmore\n");
        repo.Git("add", ".");
        repo.GitAt(DateTimeOffset.Now.AddHours(-5).ToString("o"), "commit", "-q", "-m", "Document the guide and a very long commit subject that has to be cut off at the edge");
        repo.Write("README.md", "# Atlas\nA map engine.\n");     // staged
        repo.Git("add", "README.md");
        repo.Write("src/Program.cs", "class Program { static void Main() {} }\n");   // changed
        repo.Write("src/Atlas.Core/Layer.cs", "class Layer { int Z; }\n");           // staged and changed
        repo.Git("add", "src/Atlas.Core/Layer.cs");
        repo.Write("src/Atlas.Core/Layer.cs", "class Layer { int Z; int Opacity; }\n");
        File.Delete(Path.Combine(repo.Root, "old.txt"));                                // deleted
        repo.Write("src/Atlas.Core/Tiles/TileCache.cs", "class TileCache {}\n");        // untracked
        repo.Write("notes.txt", "todo\n");                                              // untracked
        return repo;
    }

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Octopus.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("repository root not found");
    }

    private static (Window Window, ExplorerPanelView View) Open()
    {
        var view = new ExplorerPanelView();
        var window = new Window { Width = 320, Height = 760, Content = view };
        window.Show();
        Frames.Pump();
        return (window, view);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
            Frames.Pump();
        }
    }

    private static async Task Settle()
    {
        await Task.Delay(900);
        Frames.Pump();
    }

    private static void Save(Window window, string name, string theme)
    {
        Frames.Save(window, Path.Combine(s_dir, $"explorer-{name}-{theme}.png"));
    }

}
