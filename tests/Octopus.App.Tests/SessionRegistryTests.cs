using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Workspace;
using Octopus.Terminal.Avalonia;
using TKCode.Terminal.Core;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>A terminal's session belongs to its item, not to its tab (design/12-远程操作.md §4.1).</summary>
public class SessionRegistryTests
{
    private static (Octopus.Core.Projects.Project Project, Octopus.Core.Projects.ProjectItem Item) NewTerminal()
    {
        var project = AppHost.Get<IProjectService>().AddProject(Directory.CreateTempSubdirectory("octopus-session-").FullName);
        return (project, AppHost.Get<IItemOpener>().OpenTerminal()!);
    }

    private static Window Show(Control tab)
    {
        var window = new Window { Width = 700, Height = 450, Content = tab };
        window.Show();
        Frames.Pump();
        return window;
    }

    [AvaloniaFact]
    public async Task A_tab_keeps_its_session_in_the_registry_and_closing_the_tab_ends_it()
    {
        var (project, item) = NewTerminal();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        var tabs = AppHost.Get<ITabContentRegistry>();

        var window = Show(tabs.GetOrCreate(project, item));
        var session = sessions.Find(item.Id);
        Assert.NotNull(session);
        Assert.True(session.IsRunning);
        Assert.Single(factory.Started);

        await tabs.ReleaseAsync(item.Id);
        Assert.Null(sessions.Find(item.Id));
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_session_started_before_its_tab_is_taken_over_not_started_twice()
    {
        var (project, item) = NewTerminal();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        // As a view on another device would: the program starts with that device's size, and no tab exists.
        var spec = AppHost.Get<IItemLaunchService>().Plan(project, item);
        var early = sessions.GetOrStart(item.Id, spec, factory, new TerminalSize(40, 12, 0, 0),
            TerminalPalettes.Dark, ColorScheme.Dark, 1000, out var created);
        Assert.True(created);
        Assert.Single(factory.Started);

        var tab = tabs.GetOrCreate(project, item);
        var window = Show(tab);

        // The tab shows that session: nothing was started again, and the terminal now has the tab's size.
        Assert.Same(early, sessions.Find(item.Id));
        Assert.Single(factory.Started);
        var grid = early.Read(core => (core.Size.Columns, core.Size.Rows));
        Assert.True(grid.Columns > 40, $"columns: {grid.Columns}");
        Assert.True(await tabs.Find(item.Id)!.HasRunningChildAsync() == false);

        await tabs.ReleaseAsync(item.Id);
        Assert.Null(sessions.Find(item.Id));
        Assert.True(factory.Connections[0].IsEnded);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Removing_an_item_whose_tab_was_never_built_still_ends_its_session()
    {
        var (project, item) = NewTerminal();
        var sessions = AppHost.Get<TerminalSessionRegistry>();
        var factory = AppHost.Get<FakeTerminalConnectionFactory>();
        var tabs = AppHost.Get<ITabContentRegistry>();
        // A program runs in it: quitting must ask, although no tab shows it.
        factory.Foreground = "vim";
        sessions.GetOrStart(item.Id, AppHost.Get<IItemLaunchService>().Plan(project, item), factory,
            new TerminalSize(80, 24, 0, 0), TerminalPalettes.Dark, ColorScheme.Dark, 1000, out _);
        Assert.True(await tabs.AnyRunningChildAsync());

        await tabs.ReleaseAsync(item.Id);
        Assert.Null(sessions.Find(item.Id));
        Assert.True(factory.Connections[0].IsEnded);
    }
}
