using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Terminals;
using Octopus.Storage;
using Octopus.Terminal;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>The connection card belongs to terminals on an SSH host: a terminal on this computer never shows it.</summary>
public class TerminalCardTests
{
    [AvaloniaFact]
    public void A_terminal_on_this_computer_does_not_show_the_connection_card()
    {
        var project = AppHost.Get<IProjectService>().AddProject(Directory.CreateTempSubdirectory("octopus-card-").FullName);
        var item = AppHost.Get<IItemOpener>().OpenTerminal()!;
        var model = new TerminalTabViewModel(project, item, AppHost.Get<IItemLaunchService>(), AppHost.Get<ITerminalConnectionFactory>(), AppHost.Get<ISettingsStore>());
        var view = new TerminalTabView { DataContext = model };
        var window = new Window { Width = 600, Height = 400, Content = view };
        window.Show();
        Frames.Pump();

        Assert.Null(model.Connect);
        Assert.False(view.FindControl<Border>("ConnectCard")!.IsVisible);
        window.Close();
        AppHost.Get<IProjectService>().RemoveProject(project.Id);
    }
}
