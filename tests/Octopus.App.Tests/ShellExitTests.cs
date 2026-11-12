using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.App.Workspace;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>What closing the main window does: ask once, save the projects, end every terminal (design 06 section 5.6).</summary>
public class ShellExitTests
{
    private static (MainWindow Window, FakeTerminalConnectionFactory Terminals) OpenWithTerminal(string? foreground)
    {
        var terminals = AppHost.Get<FakeTerminalConnectionFactory>();
        terminals.Foreground = foreground;
        var window = new MainWindow();
        window.Show();
        AppHost.Get<IProjectService>().AddProject(Directory.CreateTempSubdirectory("octopus-exit-").FullName);
        AppHost.Get<IItemOpener>().OpenTerminal();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, terminals);
    }

    private static async Task PumpUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition(), "the condition did not become true in time");
    }

    [AvaloniaFact]
    public async Task Closing_saves_the_projects_and_ends_every_terminal_without_asking_when_only_shells_run()
    {
        var (window, terminals) = OpenWithTerminal(foreground: null);
        await PumpUntil(() => terminals.Connections.Count > 0);
        var store = AppHost.Get<RecordingProjectStore>();
        var savesBefore = store.Saves;
        var item = AppHost.Get<IProjectService>().Current!.Items.Single();

        window.Close();
        await PumpUntil(() => !window.IsVisible);

        Assert.True(store.Saves > savesBefore, "the projects were not flushed");
        Assert.All(terminals.Connections, c => Assert.True(c.IsEnded, "a terminal connection was left open"));
        Assert.Null(AppHost.Get<ITabContentRegistry>().Find(item.Id));
        Assert.Equal(0, AppHost.Get<FakeDialogService>().Asked);
    }

    [AvaloniaFact]
    public async Task A_running_program_makes_closing_ask_once_and_no_keeps_everything_open()
    {
        var (window, terminals) = OpenWithTerminal(foreground: "vim");
        await PumpUntil(() => terminals.Connections.Count > 0);
        var dialogs = AppHost.Get<FakeDialogService>();
        var store = AppHost.Get<RecordingProjectStore>();
        var savesBefore = store.Saves;

        dialogs.Answer = false;
        window.Close();
        await PumpUntil(() => dialogs.Asked == 1);
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVisible);
        Assert.Equal(savesBefore, store.Saves);
        Assert.All(terminals.Connections, c => Assert.False(c.IsEnded));

        // Saying yes the second time closes for real, after one more question.
        dialogs.Answer = true;
        window.Close();
        await PumpUntil(() => !window.IsVisible);
        Assert.Equal(2, dialogs.Asked);
        Assert.True(store.Saves > savesBefore);
        Assert.All(terminals.Connections, c => Assert.True(c.IsEnded));
    }

    [AvaloniaFact]
    public async Task Ending_everything_waits_for_the_terminals_that_a_closed_project_already_started_to_release()
    {
        var (window, terminals) = OpenWithTerminal(foreground: null);
        await PumpUntil(() => terminals.Connections.Count > 0);
        var projects = AppHost.Get<IProjectService>();

        // Removing the project releases its tabs in the background; shutting down right after must still see them end.
        projects.RemoveProject(projects.Current!.Id);
        await AppHost.Get<ShellViewModel>().ShutDownAsync();

        Assert.All(terminals.Connections, c => Assert.True(c.IsEnded));
        window.Close();
    }
}
