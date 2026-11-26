using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.App.Bots;
using Octopus.App.Bots.Groups;
using Octopus.App.Composition;
using Octopus.App.Dialogs;
using Octopus.App.Services;
using Octopus.Core.Bots;
using Octopus.Ssh;
using Xunit;

namespace Octopus.App.Tests;

public sealed class BotGroupUiTests
{
    private sealed class FixedWindowAccessor(TopLevel window) : IWindowAccessor
    {
        public TopLevel? Window { get; set; } = window;
    }

    [AvaloniaFact]
    public async Task NewGroupDialog_opens_cleanly_and_toggles_members_without_error()
    {
        var window = new Window { Width = 1000, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var dialogs = new DialogService(
            new FixedWindowAccessor(window),
            AppHost.Get<IPickerService>(),
            AppHost.Get<ISshHostCatalog>(),
            AppHost.Get<IProjectIconStore>());

        var bot1 = new BotDefinition { Id = "b1", Name = "小李", AgentId = "claude", Color = "#d97757" };
        var bot2 = new BotDefinition { Id = "b2", Name = "小王", AgentId = "codex", Color = "#5b5bd6" };

        var task = dialogs.AskNewBotGroupAsync([bot1, bot2]);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        if (task.IsFaulted)
        {
            throw task.Exception!;
        }

        Assert.False(task.IsCompleted, "Dialog should stay open waiting for user action.");

        // Also validate editing an existing group does not throw during construction or toggle.
        var dialogVm = new BotGroupDialogViewModel([bot1, bot2]);
        Assert.Equal(2, dialogVm.SelectedCount);
        Assert.True(dialogVm.CanConfirm);
        dialogVm.Members[0].ToggleCommand.Execute(null);
        Assert.Equal(1, dialogVm.SelectedCount);
        dialogVm.Members[1].ToggleCommand.Execute(null);
        Assert.Equal(0, dialogVm.SelectedCount);
        Assert.False(dialogVm.CanConfirm);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void BotGroupRoomView_and_RightPanelView_initialize_cleanly()
    {
        var window = new Window { Width = 1000, Height = 700 };
        var roomVm = AppHost.Get<BotGroupRoomViewModel>();
        var rightVm = AppHost.Get<BotGroupRightPanelViewModel>();
        var panelVm = AppHost.Get<BotPanelViewModel>();

        var host = new StackPanel();
        host.Children.Add(new BotPanelView { DataContext = panelVm });
        host.Children.Add(new BotGroupRoomView { DataContext = roomVm });
        host.Children.Add(new BotGroupRightPanelView { DataContext = rightVm });
        window.Content = host;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.Close();
    }
}
