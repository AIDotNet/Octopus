using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Octopus.App.Composition;
using Octopus.App.Plugins;
using Octopus.App.Plugins.Rendering;
using Octopus.App.Services;
using Octopus.Core.Plugins;
using Octopus.Core.Projects;
using Octopus.Plugins;
using Octopus.Plugins.Runtime;
using Octopus.Storage;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using Xunit;

namespace Octopus.App.Tests.Plugins;

/// <summary>The app side of plugins: the renderer, a plugin view tab end to end, indicators and color themes.</summary>
public sealed class PluginViewTests
{
    private sealed class RecordingSink : IUiEventSink
    {
        public List<(string Handler, string Args)> Fired { get; } = [];
        public List<string> Commands { get; } = [];
        public void Fire(string handlerId, JsonArray args) => Fired.Add((handlerId, args.ToJsonString()));
        public void ExecuteCommand(string commandId, JsonArray args) => Commands.Add(commandId);
        public Task<string?> PickPathAsync(bool folder, string? title) => Task.FromResult<string?>(null);
    }

    private static JsonObject Tree(string text, string? key = null) => JsonNode.Parse($$"""
        { "t": "Column", "p": { "gap": 2 }, "c": [
          { "t": "Text", "p": { "value": "{{text}}" } },
          { "t": "Input", "k": "{{key ?? "name"}}", "p": { "value": "start", "onChange": { "$h": "r/1:onChange" } } },
          { "t": "Button", "p": { "label": "Go", "onClick": { "$h": "r/2:onClick" } } },
          { "t": "Mystery", "p": {} }
        ] }
        """)!.AsObject();

    [AvaloniaFact]
    public void Renderer_KeepsControlsAcrossRenders_AndRoutesEvents()
    {
        var sink = new RecordingSink();
        var renderer = new UiRenderer(sink);
        var first = renderer.Render(Tree("one"), null);
        var window = new Window { Content = first };
        window.Show();
        var input = first.GetLogicalDescendants().OfType<Input>().Single();
        input.Text = "typed";

        var second = renderer.Render(Tree("two"), first);
        Frames.Pump();
        // Same control: the typing survives a re-render whose value did not change rev (design 05 §2).
        Assert.Same(first, second);
        Assert.Same(input, second.GetLogicalDescendants().OfType<Input>().Single());
        Assert.Equal("typed", input.Text);
        Assert.Contains(second.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "two");

        second.GetLogicalDescendants().OfType<TKCode.Arc.Button>().Single(b => Equals(b.Content, "Go"))
            .RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.Contains(sink.Fired, f => f.Handler == "r/2:onClick");
        // An unknown component shows a placeholder instead of failing the whole view.
        Assert.Contains(second.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text?.Contains("Mystery") == true);
        window.Close();
    }

    [AvaloniaFact]
    public void Renderer_ANewKey_GivesANewControl()
    {
        var renderer = new UiRenderer(new RecordingSink());
        var first = renderer.Render(Tree("one", key: "a"), null);
        var input = first.GetLogicalDescendants().OfType<Input>().Single();
        var second = renderer.Render(Tree("one", key: "b"), first);
        Assert.NotSame(input, second.GetLogicalDescendants().OfType<Input>().Single());
    }

    private const string CounterPlugin = """
        import { commands, indicators, ui, views } from 'octopus';
        export function activate(ctx) {
          let count = 0;
          ctx.subscriptions.push(views.register('test.counter.main', {
            open: view => ({
              render: () => ui.column({ gap: 2 }, [
                ui.text(`count ${count}`),
                ui.button({ label: 'Add', onClick: () => { count++; view.invalidate(); view.setTitle(`Counter ${count}`); } }),
              ]),
              getState: () => ({ count }),
            }),
          }));
          const indicator = indicators.create('test.counter.status');
          indicator.update({ text: 'ready', level: 'warning' });
          ctx.subscriptions.push(indicator);
        }
        """;

    private static async Task<(PluginManager Manager, Project Project)> InstallAsync(string id, string main, string contributes)
    {
        var paths = AppHost.Get<PluginPaths>();
        var directory = paths.PackageDirectoryOf(id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            { "schema": 1, "id": "{{id}}", "name": "{{id}}", "version": "1.0.0", "engines": { "api": 0 }, "main": "main.js",
              "contributes": {{contributes}} }
            """);
        File.WriteAllText(Path.Combine(directory, "main.js"), main);
        AppHost.Get<IPluginStateStore>().Update(d => d.Plugins[id] = new PluginRecord { Enabled = true, Source = PluginSource.File, Version = "1.0.0" });
        var manager = AppHost.Get<PluginManager>();
        await manager.StartAsync();
        var project = AppHost.Get<IProjectService>().AddProject(Directory.CreateTempSubdirectory("octopus-plugin-project-").FullName);
        return (manager, project);
    }

    [AvaloniaFact]
    public async Task ViewTab_Opens_Renders_HandlesClicks_AndKeepsItsState()
    {
        var (manager, project) = await InstallAsync("test.counter", CounterPlugin, """
            { "views": [ { "id": "test.counter.main", "title": "Counter", "launcher": true, "singleton": "project" } ],
              "indicators": [ { "id": "test.counter.status", "title": "Status" } ] }
            """);
        var workbench = AppHost.Get<PluginWorkbench>();
        var (host, view) = workbench.FindView("test.counter.main")!.Value;
        var item = workbench.OpenView(host, view)!;
        // A project singleton: opening it again shows the same tab.
        Assert.Same(item, workbench.OpenView(host, view));
        Assert.Contains(workbench.LauncherViews, v => v.View.Id == "test.counter.main");

        var tab = new PluginViewTab(project, item, workbench, AppHost.Get<IProjectService>());
        var window = new Window { Content = tab, Width = 800, Height = 600 };
        window.Show();
        Assert.True(Frames.Until(() => tab.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "count 0")), "The view did not render.");

        tab.GetLogicalDescendants().OfType<TKCode.Arc.Button>().Single(b => Equals(b.Content, "Add"))
            .RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Assert.True(Frames.Until(() => tab.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "count 1")), "The click did not re-render.");
        Assert.True(Frames.Until(() => item.Title == "Counter 1"), "setTitle did not reach the tab.");

        // The indicator the plugin created is in the strip with its text.
        var indicators = AppHost.Get<PluginIndicators>();
        Assert.True(Frames.Until(() => indicators.Items.Any(i => i.Id == "test.counter.status" && i.Text == "ready")));

        var close = tab.CloseAsync();
        Assert.True(Frames.Until(() => close.IsCompleted));
        await close;
        Assert.Equal("""{"count":1}""", item.ViewState);
        window.Close();
        _ = manager;
    }

    [AvaloniaFact]
    public async Task ViewTab_WithoutItsPlugin_SaysSo()
    {
        var project = AppHost.Get<IProjectService>().AddProject(Directory.CreateTempSubdirectory("octopus-plugin-project-").FullName);
        await AppHost.Get<PluginManager>().StartAsync();
        var item = new ProjectItem { Kind = ProjectItemKind.Plugin, ViewId = "gone.view", Title = "Gone" };
        AppHost.Get<IProjectService>().AddItem(project, item);
        var tab = new PluginViewTab(project, item, AppHost.Get<PluginWorkbench>(), AppHost.Get<IProjectService>());
        var window = new Window { Content = tab };
        window.Show();
        Assert.True(Frames.Until(() => tab.GetLogicalDescendants().OfType<EmptyState>().Any()));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Themes_ApplyTheChosenColors_AndRefuseUnreadableOnes()
    {
        var (_, _) = await InstallAsync("test.theme", "export function activate() {}", """
            { "themes": [
                { "id": "test.theme.red", "label": "Red", "appearance": "light", "path": "red.json" },
                { "id": "test.theme.bad", "label": "Bad", "appearance": "light", "path": "bad.json" } ] }
            """);
        var directory = AppHost.Get<PluginPaths>().PackageDirectoryOf("test.theme");
        File.WriteAllText(Path.Combine(directory, "red.json"), """{ "schema": 1, "colors": { "Background": "#fff0f0", "Surface": "#ffe0e0" } }""");
        File.WriteAllText(Path.Combine(directory, "bad.json"), """{ "schema": 1, "colors": { "Foreground": "#fefefe", "Background": "#ffffff" } }""");
        AppHost.Get<Octopus.Plugins.Catalog.IPluginCatalog>().Refresh();
        var themes = AppHost.Get<PluginThemes>();
        ArcTheme.Apply(b => b.Appearance(ArcAppearance.Light));

        themes.Choose(ThemeAppearance.Light, "test.theme/test.theme.red");
        themes.Apply();
        Assert.Null(themes.Problem);
        Assert.Equal(Avalonia.Media.Color.Parse("#fff0f0"), Token(ArcDesignTokenKind.Background));

        themes.Choose(ThemeAppearance.Light, "test.theme/test.theme.bad");
        themes.Apply();
        Assert.NotNull(themes.Problem);
        // Refused: the previous colors stay.
        Assert.Equal(Avalonia.Media.Color.Parse("#fff0f0"), Token(ArcDesignTokenKind.Background));

        themes.Choose(ThemeAppearance.Light, null);
        themes.Apply();
        Assert.NotEqual(Avalonia.Media.Color.Parse("#fff0f0"), Token(ArcDesignTokenKind.Background));
    }

    private static Avalonia.Media.Color? Token(ArcDesignTokenKind kind)
    {
        Application.Current!.TryGetResource(ArcDesignTokens.Key(kind), Application.Current.ActualThemeVariant, out var value);
        return value switch
        {
            Avalonia.Media.ISolidColorBrush brush => brush.Color,
            Avalonia.Media.Color color => color,
            _ => null,
        };
    }
}
