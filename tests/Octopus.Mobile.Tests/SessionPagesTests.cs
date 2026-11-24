using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Octopus.Mobile.Common;
using Octopus.Mobile.Conversations;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Sessions;
using Octopus.Mobile.Terminals;
using Octopus.Mobile.Workspace;
using TKCode.Arc.Mobile;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>
/// The pages of design/ui/移动端功能扩展方案.html: the terminal above the keyboard (K-A) with its "⋯" menu (②), a session's
/// long-press menu and details page (M-C), GUI sessions (G-0), and new projects from a host's folders.
/// </summary>
public class SessionPagesTests
{
    private static (TerminalPage Page, TerminalViewModel ViewModel, RemoteSession Session) OpenTerminal(Phone phone, int project, int session)
    {
        var target = phone.Workspace.Hosts[0].Projects[project].Sessions[session];
        phone.Shell.ShowHost(target.Project.Host);
        phone.Shell.ShowSession(target);
        Assert.True(Phone.Until(() => phone.Top<TerminalPage>().ReadScreenText().Trim().Length > 0));
        var page = phone.Top<TerminalPage>();
        return (page, (TerminalViewModel)page.DataContext!, target);
    }

    private static double BottomOf(Control control, Visual root) => control.TranslatePoint(new Point(0, control.Bounds.Height), root)!.Value.Y;

    [AvaloniaFact]
    public void The_input_stays_above_the_keyboard_and_the_terminal_takes_what_is_left()
    {
        using var phone = new Phone();
        var keyboard = new KeyboardInsetSource();
        TKCode.Arc.Mobile.Page.SetKeyboardInsetSource(phone.Shell, keyboard);
        var (page, terminal, _) = OpenTerminal(phone, 0, 1);
        terminal.FitCommand.Execute(null);
        Phone.Pump();
        Assert.True(terminal.IsComposing);
        var bars = page.FindControl<Control>("InputBars")!;
        var composer = page.FindControl<Control>("Composer")!;
        var rowsBefore = page.GridSize!.Value.Rows;

        const double inset = 300;
        keyboard.Set(inset, TimeSpan.Zero);
        Phone.Settle(600);

        // The bars (the composer in them) end where the keyboard begins, not behind it.
        var top = phone.Window.Height - inset;
        Assert.True(BottomOf(bars, phone.Window) <= top + 1, $"the input bars end at {BottomOf(bars, phone.Window)}, the keyboard starts at {top}");
        Assert.True(BottomOf(composer, phone.Window) <= top, "the composer is above the keyboard");
        Assert.True(composer.IsEffectivelyVisible);
        // The terminal gave up the rows the keyboard covers.
        Assert.True(page.GridSize!.Value.Rows < rowsBefore, $"{page.GridSize!.Value.Rows} rows with the keyboard, {rowsBefore} without");
    }

    [AvaloniaFact]
    public void The_terminal_menu_fits_sizes_text_and_acts_on_the_session()
    {
        using var phone = new Phone();
        var (page, terminal, session) = OpenTerminal(phone, 0, 2);
        var overlays = page.FindControl<SessionActionOverlays>("Overlays")!;

        terminal.OpenMenuCommand.Execute(null);
        Phone.Pump();
        Assert.True(overlays.IsMenuOpen);
        Assert.Equal(["适应手机", "放大字号", "缩小字号", "浏览文件", "查看改动", "重命名", "重启程序", "用相同设置再开一个", "会话详情", "关闭会话"], overlays.MenuLabels);

        terminal.LargerTextCommand.Execute(null);
        Assert.Equal(TerminalViewModel.DefaultFontSize + 1, terminal.FontSize);
        Assert.Equal(TerminalViewModel.DefaultFontSize + 1, page.FindControl<Octopus.Terminal.Avalonia.TerminalControl>("Terminal")!.FontSize);

        // Fitted: the first row gives the size back.
        terminal.FitCommand.Execute(null);
        terminal.OpenMenuCommand.Execute(null);
        Assert.Equal("交还尺寸", overlays.MenuLabels[0]);

        terminal.Actions.RestartCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Workspace.Restarted.Contains(session.Id)));

        // Closed from its own page: the page leaves and the session is gone.
        terminal.Actions.BeginCloseCommand.Execute(null);
        Assert.Equal("关闭“dotnet watch”？", terminal.Actions.CloseTitle);
        terminal.Actions.ConfirmCloseCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostPage));
        Assert.DoesNotContain(session, phone.Workspace.Hosts[0].Projects[0].Sessions);
    }

    [AvaloniaFact]
    public void Holding_a_session_row_opens_its_menu_without_opening_the_session()
    {
        using var phone = new Phone();
        var mac = phone.Workspace.Hosts[0];
        phone.Shell.ShowHost(mac);
        Phone.Settle(400);
        var page = phone.Top<HostPage>();
        var row = page.GetVisualDescendants().OfType<ListItem>().First(item => item.DataContext is SessionRowViewModel);
        var session = ((SessionRowViewModel)row.DataContext!).Session;

        // A real long press (a held mouse button counts, as in the desktop preview), then the release.
        var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), phone.Window)!.Value;
        phone.Window.MouseDown(point, MouseButton.Left);
        Phone.Settle(900);
        phone.Window.MouseUp(point, MouseButton.Left);
        Phone.Settle(300);

        Assert.IsType<HostPage>(phone.Shell.Top);
        var overlays = page.FindControl<SessionActionOverlays>("SessionOverlays")!;
        Assert.True(overlays.IsMenuOpen);
        Assert.Equal(["打开", "重命名", "重启程序", "用相同设置再开一个", "会话详情", "关闭会话"], overlays.MenuLabels);
        var host = (HostViewModel)page.DataContext!;
        Assert.Same(session, host.Actions.Session);

        // Details from the menu.
        host.Actions.ShowDetailsCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is SessionDetailPage));
        var details = (SessionDetailViewModel)phone.Top<SessionDetailPage>().DataContext!;
        Assert.Same(session, details.Session);
        Assert.Equal("MacBook Pro", details.HostText);
        Assert.Equal("终端", details.SurfaceText);
        Assert.True(details.CanOpenTerminal);

        // Another like it: same agent, model and effort, opened at once.
        var before = session.Project.Sessions.Count;
        details.Actions.DuplicateCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is TerminalPage));
        Assert.Equal(before + 1, session.Project.Sessions.Count);
        var copy = session.Project.Sessions[^1];
        Assert.Equal((session.AgentId, session.Model, session.Effort), (copy.AgentId, copy.Model, copy.Effort));
    }

    [AvaloniaFact]
    public void A_gui_session_opens_its_conversation_when_supported_and_details_show_open_conversation()
    {
        using var phone = new Phone();
        var station = phone.Workspace.Hosts.Single(host => host.Id == "station");
        var gui = station.Projects[0].Sessions.Single(session => session.IsGui);
        phone.Shell.ShowHost(station);
        phone.Shell.ShowSession(gui);
        Phone.Pump();

        var conversation = phone.Top<ConversationPage>();
        var vm = (ConversationViewModel)conversation.DataContext!;
        Assert.Same(gui, vm.Session);
        Assert.True(vm.IsLoaded);

        // Session details are still reachable from the menu, and offer "打开对话" without the outdated-host notice.
        vm.Actions.ShowDetailsCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is SessionDetailPage));
        var page = phone.Top<SessionDetailPage>();
        var details = (SessionDetailViewModel)page.DataContext!;
        Assert.True(details.IsGui);
        Assert.True(details.CanOpenConversation);
        Assert.False(details.ShowGuiNotice);
        Assert.False(details.CanOpenTerminal);
        Assert.Equal("GUI 对话", details.SurfaceText);
        Assert.True(page.FindControl<Control>("OpenConversationRow")!.IsVisible);
        Assert.False(page.FindControl<Control>("GuiNotice")!.IsVisible);
        Assert.False(page.FindControl<Control>("OpenTerminalRow")!.IsVisible);
        Assert.False(details.Actions.CanRestart);
        Assert.False(page.FindControl<Control>("RestartRow")!.IsVisible);

        details.OpenConversationCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is ConversationPage));
    }

    [AvaloniaFact]
    public void A_gui_session_on_an_older_host_opens_its_details_with_a_notice()
    {
        using var phone = new Phone();
        phone.Workspace.ConversationsSupported = false;
        var station = phone.Workspace.Hosts.Single(host => host.Id == "station");
        var gui = station.Projects[0].Sessions.Single(session => session.IsGui);
        phone.Shell.ShowHost(station);
        phone.Shell.ShowSession(gui);
        Phone.Pump();

        var page = phone.Top<SessionDetailPage>();
        var details = (SessionDetailViewModel)page.DataContext!;
        Assert.True(details.IsGui);
        Assert.False(details.CanOpenConversation);
        Assert.True(details.ShowGuiNotice);
        Assert.False(details.CanOpenTerminal);
        Assert.Equal("GUI 对话", details.SurfaceText);
        Assert.True(page.FindControl<Control>("GuiNotice")!.IsVisible);
        Assert.False(page.FindControl<Control>("OpenConversationRow")!.IsVisible);
        Assert.False(page.FindControl<Control>("OpenTerminalRow")!.IsVisible);
        // No terminal, so no restart.
        Assert.False(details.Actions.CanRestart);
        Assert.False(page.FindControl<Control>("RestartRow")!.IsVisible);

        details.Actions.BeginRenameCommand.Execute(null);
        details.Actions.RenameText = "API 文档";
        details.Actions.ConfirmRenameCommand.Execute(null);
        Assert.True(Phone.Until(() => gui.Title == "API 文档"));
        Assert.Equal("API 文档", details.Title);
    }

    [AvaloniaFact]
    public void A_new_project_is_picked_from_the_hosts_folders()
    {
        using var phone = new Phone();
        var mac = phone.Workspace.Hosts[0];
        phone.Shell.ShowHost(mac);
        var host = (HostViewModel)phone.Top<HostPage>().DataContext!;

        // With projects, "+" asks which; the folder picker starts at home.
        host.OpenAddMenuCommand.Execute(null);
        Assert.True(host.IsAddMenuOpen);
        host.NewProjectCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is FolderPickerPage));
        var picker = (FolderPickerViewModel)phone.Top<FolderPickerPage>().DataContext!;
        Assert.True(Phone.Until(() => !picker.IsLoading && picker.Folders.Count == 3));
        Assert.Equal("/Users/demo", picker.Path);
        Assert.Equal(["主目录", "/"], picker.Roots.Select(root => root.Name));

        picker.Folders.Single(folder => folder.Name == "code").OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => picker.Path == "/Users/demo/code" && !picker.IsLoading));
        // The computer's projects are marked.
        Assert.Equal(["Octopus", "OctopusServer", "TKCode.Arc"], picker.Folders.Where(folder => folder.IsProject).Select(folder => folder.Name));

        // A new folder, then that folder as the project.
        picker.BeginNewFolderCommand.Execute(null);
        Assert.True(picker.IsNewFolderOpen);
        picker.NewFolderName = "mobile-app";
        picker.CreateFolderCommand.Execute(null);
        Assert.True(Phone.Until(() => picker.Path == "/Users/demo/code/mobile-app" && !picker.IsLoading));
        Assert.Equal("把“mobile-app”设为项目", picker.ChooseText);
        picker.ChooseCommand.Execute(null);

        Assert.True(Phone.Until(() => phone.Shell.Top is HostPage));
        Assert.Contains(mac.Projects, project => project.Path == "~/code/mobile-app");
        Assert.Equal(4, host.Sections.Count);
        Assert.True(host.NewSession.IsOpen);
        Assert.Equal("~/code/mobile-app", host.NewSession.Projects.Single(choice => choice.IsSelected).Path);
    }

    [AvaloniaFact]
    public void A_long_session_title_is_trimmed_between_the_back_button_and_the_menu()
    {
        using var phone = new Phone();
        var (page, _, session) = OpenTerminal(phone, 0, 1);
        session.Title = string.Concat(Enumerable.Repeat("一个非常非常长的会话名称 ", 12));

        var navbar = page.GetVisualDescendants().OfType<Navbar>().Single();
        var back = navbar.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Back");
        var title = navbar.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "PART_Title");
        var menu = page.FindControl<Control>("MenuButton")!;

        Rect BoundsOf(Control control) => new(control.TranslatePoint(default, phone.Window)!.Value, control.Bounds.Size);
        // Wait for the page to finish sliding in and the new title to finish swapping in.
        Phone.Until(() => Math.Abs(BoundsOf(navbar).X) < 0.5 && BoundsOf(title).Width > phone.Window.Width / 3);
        var (backBounds, titleBounds, menuBounds) = (BoundsOf(back), BoundsOf(title), BoundsOf(menu));

        // Both buttons keep their place on screen, and the title sits between them.
        Assert.True(backBounds.Width > 0 && backBounds.X >= 0, $"back at {backBounds}");
        Assert.True(menuBounds.Width > 0 && menuBounds.Right <= phone.Window.Width + 0.5, $"menu at {menuBounds}");
        Assert.True(titleBounds.X >= backBounds.Right - 0.5, $"title at {titleBounds}, back ends at {backBounds.Right}");
        Assert.True(titleBounds.Right <= menuBounds.X + 0.5, $"title at {titleBounds}, menu starts at {menuBounds.X}");

        // And a tap on the menu reaches it.
        var hit = phone.Window.InputHitTest(menuBounds.Center) as Visual;
        Assert.True(hit is not null && (hit == menu || hit.GetVisualAncestors().Contains(menu)), $"a tap on the menu hits {hit}");
    }
}
