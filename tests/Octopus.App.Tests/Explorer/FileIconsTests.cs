using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.App.Common;
using Xunit;

namespace Octopus.App.Tests.Explorer;

public class FileIconsTests
{
    [AvaloniaFact]
    public void Every_bundled_file_icon_parses_into_valid_geometry_shapes()
    {
        var store = FileIconStore.Instance;
        var ids = store.AllIconIds.ToList();
        Assert.NotEmpty(ids);

        foreach (var id in ids)
        {
            var graphic = store.Get(id, isLight: false);
            Assert.NotNull(graphic);
            Assert.NotEmpty(graphic!.Shapes);
            Assert.True(graphic.ViewBox.Width > 0 && graphic.ViewBox.Height > 0);
        }
    }

    [AvaloniaFact]
    public void Resolves_exact_names_compound_extensions_single_extensions_and_semantic_folders()
    {
        // Exact filenames
        Assert.Equal("docker", FileIcons.KeyFor("Dockerfile", isDirectory: false));
        Assert.Equal("nodejs", FileIcons.KeyFor("package.json", isDirectory: false));
        Assert.Equal("nuget", FileIcons.KeyFor("Directory.Packages.props", isDirectory: false));
        Assert.Equal("git", FileIcons.KeyFor(".gitignore", isDirectory: false));
        Assert.Equal("readme", FileIcons.KeyFor("README.md", isDirectory: false));

        // Compound & single extensions
        Assert.Equal("typescript-def", FileIcons.KeyFor("globals.d.ts", isDirectory: false));
        Assert.Equal("csharp", FileIcons.KeyFor("Program.cs", isDirectory: false));
        Assert.Equal("xaml", FileIcons.KeyFor("FileTreeView.axaml", isDirectory: false));
        Assert.Equal("typescript", FileIcons.KeyFor("index.ts", isDirectory: false));
        Assert.Equal("react_ts", FileIcons.KeyFor("App.tsx", isDirectory: false));
        Assert.Equal("python", FileIcons.KeyFor("script.py", isDirectory: false));
        Assert.Equal("rust", FileIcons.KeyFor("main.rs", isDirectory: false));
        Assert.Equal("go", FileIcons.KeyFor("server.go", isDirectory: false));
        Assert.Equal("json", FileIcons.KeyFor("config.json", isDirectory: false));
        Assert.Equal("markdown", FileIcons.KeyFor("NOTES.md", isDirectory: false));

        // Semantic folders (closed vs open) and fallback folder
        Assert.Equal("folder-src", FileIcons.KeyFor("src", isDirectory: true, isExpanded: false));
        Assert.Equal("folder-src-open", FileIcons.KeyFor("src", isDirectory: true, isExpanded: true));
        Assert.Equal("folder-test", FileIcons.KeyFor("tests", isDirectory: true, isExpanded: false));
        Assert.Equal("folder-test-open", FileIcons.KeyFor("tests", isDirectory: true, isExpanded: true));
        Assert.Equal("folder", FileIcons.KeyFor("some_unknown_dir", isDirectory: true, isExpanded: false));
        Assert.Equal("folder-open", FileIcons.KeyFor("some_unknown_dir", isDirectory: true, isExpanded: true));

        // Light variant resolves cleanly
        Assert.NotNull(FileIconStore.Instance.Get("toml", isLight: true));
    }

    [AvaloniaFact]
    public void FileIconView_renders_in_a_window_without_creating_child_controls()
    {
        var icon = new FileIconView { IconKey = "csharp", Size = 16 };
        var window = new Window { Width = 64, Height = 64, Content = icon };
        window.Show();
        Frames.Pump();

        Assert.Equal(16, icon.DesiredSize.Width);
        Assert.Equal(16, icon.DesiredSize.Height);

        icon.IconKey = "folder-src-open";
        Frames.Pump();
        window.Close();
    }
}
