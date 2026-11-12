using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Octopus.App.Composition;
using Octopus.App.Files;
using Octopus.App.Tests.Explorer;
using Octopus.Core.Projects;
using Octopus.FileSystem;
using Octopus.Git;
using Octopus.Storage;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests.Files;

/// <summary>Renders the diff tab and the file tab with Skia on the headless platform and saves PNGs to <c>bin/shots</c> for a person to look at.</summary>
public class DiffScreenshots
{
    private static readonly string s_dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "shots"));

    [AvaloniaFact]
    public async Task Render_the_diff_and_file_tabs()
    {
        if (ExplorerFixture.FindGit() is null) return;
        Directory.CreateDirectory(s_dir);
        using var repo = new ExplorerFixture();
        repo.InitRepository();
        var original = """
            using System;
            using System.Collections.Generic;
            using System.Text;

            namespace Octopus.Demo;

            /// <summary>A small least-recently-used cache.</summary>
            public sealed class Cache<TKey, TValue> where TKey : notnull
            {
                private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = new();
                private readonly LinkedList<(TKey Key, TValue Value)> _order = new();
                private readonly int _capacity;
                    // line 8 of the original file

                public Cache(int capacity)
                {
                    if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
                    _capacity = capacity;
                }

                public int Count => _items.Count;

                public bool TryGet(TKey key, out TValue? value)
                {
                    if (_map.TryGetValue(key, out var node))
                    {
                        _order.Remove(node);
                        _order.AddFirst(node);
                        value = node.Value.Value;
                        return true;
                    }
                    value = default;
                    return false;
                }

                public void Set(TKey key, TValue value)
                {
                    // line 31 of the original file
                    if (_map.Remove(key, out var existing)) _order.Remove(existing);
                    _order.AddFirst((key, value));
                    _map[key] = _order.First!;
                    if (_map.Count > _capacity)
                    {
                        _map.Remove(_order.Last!.Value.Key);
                        _order.RemoveLast();
                    }
                }
            }
            """.Replace("\r\n", "\n") + "\n";
        repo.Write("src/Cache.cs", original);
        repo.Write("big.txt", "x\n");
        repo.Write("data.bin", "x");
        repo.Git("add", ".");
        repo.Git("commit", "-q", "-m", "first");
        var changed = original
            .Replace("using System.Text;", "using System.Text;\nusing System.Linq;")
            .Replace("    public int Count => _items.Count;", "    public int Count => _map.Count; // 日本語と中文のコメントも並ぶ\n    public bool IsEmpty => Count == 0;")
            .Replace("    // line 31 of the original file", "    // " + new string('w', 220))
            .Replace("    // line 8 of the original file\n", "");
        repo.Write("src/Cache.cs", changed);
        repo.Write("big.txt", string.Concat(Enumerable.Range(0, GitDiffParser.MaxLines + 1200).Select(i => $"row {i}\n")));
        File.WriteAllBytes(Path.Combine(repo.Root, "data.bin"), [0, 1, 2]);
        repo.Write("notes.txt", "todo: one\ntodo: two\n");

        foreach (var dark in new[] { false, true })
        {
            ArcTheme.Apply(t => t.Appearance(dark ? ArcAppearance.Dark : ArcAppearance.Light));
            var suffix = dark ? "dark" : "light";
            await Shot("diff", suffix, new DiffTabRequest(repo.Root, "src/Cache.cs", Path.Combine(repo.Root, "src/Cache.cs"), false), 1100, 760);
            await Shot("diff-truncated", suffix, new DiffTabRequest(repo.Root, "big.txt", Path.Combine(repo.Root, "big.txt"), false), 760, 360);
            await Shot("diff-binary", suffix, new DiffTabRequest(repo.Root, "data.bin", Path.Combine(repo.Root, "data.bin"), false), 760, 220);
            await Shot("diff-untracked", suffix, new DiffTabRequest(repo.Root, "notes.txt", Path.Combine(repo.Root, "notes.txt"), false), 760, 200);

            repo.Write("package.json", "{\n  \"name\": \"octopus\",\n  \"version\": \"1.4.2\",\n  \"private\": true,\n  \"scripts\": {\n    \"build\": \"dotnet build\",\n    \"test\": \"dotnet test --no-build\"\n  },\n  \"keywords\": [\"terminal\", \"agents\", \"projects\"],\n  \"engines\": { \"node\": \">=20\", \"dotnet\": 11 },\n  \"enabled\": true,\n  \"owner\": null\n}\n");
            repo.Write("docs/long.txt", "A short first line.\n" + string.Join(' ', Enumerable.Range(1, 90).Select(i => $"word{i}")) + "\n\tIndented with a tab and then a very long stretch of text that keeps going past the edge of the window so that wrapping has something to do " + new string('x', 160) + "\nThe end.\n");
            await FileShot("file-json", suffix, repo, "package.json", 1100, 760);
            await FileShot("file-csharp", suffix, repo, "src/Cache.cs", 1100, 760, selectAndHover: true);
            await FileShot("file-wrap-off", suffix, repo, "docs/long.txt", 1100, 360);
            await FileShot("file-wrap-on", suffix, repo, "docs/long.txt", 1100, 360, wrap: true);

            // A Markdown file: the rendered preview, then the source.
            repo.Write("README.md", "# Octopus\n\nA **terminal** and project manager for *coding agents*.\n\n- one\n- two with `code`\n\n> quoted\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n```cs\nvar x = 42; // fenced\n```\n\n## Details\n\nSome longer paragraph text so that the comfortable reading width can be judged: " + string.Join(' ', Enumerable.Repeat("lorem ipsum dolor sit amet", 12)) + "\n");
            await FileShot("markdown-preview", suffix, repo, "README.md", 1100, 760);
            await FileShot("markdown-source", suffix, repo, "README.md", 1100, 760, source: true);
        }
    }

    private static async Task FileShot(string name, string suffix, ExplorerFixture repo, string relative, double width, double height,
        bool wrap = false, bool source = false, bool selectAndHover = false)
    {
        var path = Path.Combine(repo.Root, relative);
        var model = new FileTabViewModel(new ProjectItem { Kind = ProjectItemKind.File, FilePath = path },
            AppHost.Get<IFileTreeService>(), AppHost.Get<ISettingsStore>(), repo.Root);
        if (model.Viewer is Octopus.App.Files.Code.CodeViewerModel codeModel) codeModel.WrapLines = wrap;
        if (source && model.Viewer is Octopus.App.Files.Documents.MarkdownViewerModel markdown) markdown.ViewMode = Octopus.App.Files.Documents.MarkdownViewerModel.SourceMode;
        var view = new FileTabView { DataContext = model };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        for (var i = 0; i < 60 && !Loaded(model.Viewer); i++) { await Task.Delay(25); Frames.Pump(); }
        await Task.Delay(700);
        Frames.Pump();
        if (selectAndHover)
        {
            var code = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<CodeView>().First();
            code.Select(3, 4, 5, 20);
            window.MouseMove(new Avalonia.Point(code.GutterWidth + 300, code.RowHeight * 8.5 + 32));
            Frames.Pump();
        }
        Frames.Save(window, Path.Combine(s_dir, $"{name}-{suffix}.png"));
        window.Close();
        model.Dispose();
    }

    private static bool Loaded(FileViewerModel viewer) => viewer switch
    {
        Octopus.App.Files.Code.CodeViewerModel code => code.Text.Length > 0,
        Octopus.App.Files.Documents.MarkdownViewerModel markdown => markdown.Text.Length > 0,
        _ => true,
    };

    private static async Task Shot(string name, string suffix, DiffTabRequest request, double width, double height)
    {
        using var model = new DiffTabViewModel(request, AppHost.Get<IGitService>(), AppHost.Get<IFileTreeService>(), AppHost.Get<ISettingsStore>());
        var window = new Window { Width = width, Height = height, Content = new DiffTabView { DataContext = model } };
        window.Show();
        for (var i = 0; i < 100 && model.Rows.Count == 0 && model.Notice is null; i++)
        {
            await Task.Delay(25);
            Frames.Pump();
        }
        await Task.Delay(300);
        Frames.Save(window, Path.Combine(s_dir, $"diff-tab-{name}-{suffix}.png"));
        window.Close();
    }
}
