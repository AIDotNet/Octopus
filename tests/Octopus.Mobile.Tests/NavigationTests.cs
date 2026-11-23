using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Octopus.Mobile.Account;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Terminals;
using Octopus.Mobile.Workspace;
using TKCode.Arc.Mobile;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>The hierarchy of layout A: hosts, one host's projects and sessions, one session's terminal.</summary>
public class NavigationTests
{
    [AvaloniaFact]
    public void The_first_page_groups_the_hosts_and_says_what_waits()
    {
        using var phone = new Phone();
        var hosts = (HostsViewModel)phone.Top<HostsPage>().DataContext!;

        Assert.Equal(["MacBook Pro", "工作站"], hosts.Online.Select(row => row.Name));
        Assert.Equal(["Mac mini"], hosts.Offline.Select(row => row.Name));
        Assert.Equal(["Linux 服务器"], hosts.Unpaired.Select(row => row.Name));

        var mac = hosts.Online[0];
        Assert.Equal("局域网 · 3 个项目", mac.Summary);
        Assert.True(mac.IsWaiting);
        Assert.Equal("1 等你", mac.PillText);
        // Nothing waits or works on the second one.
        Assert.True(hosts.Online[1].IsIdle);
        Assert.False(hosts.Offline[0].OpenCommand.CanExecute(null));
        Assert.Equal("配对", hosts.Unpaired[0].PillText);

        // Every host is a row on screen, and after them the way to add one by hand.
        Assert.Equal(5, phone.Shell.GetVisualDescendants().OfType<ListItem>().Count());
        Assert.False(hosts.IsEmpty);
        Assert.Equal("Demo", hosts.AccountName);
        hosts.OpenAccountCommand.Execute(null);
        Assert.True(hosts.IsAccountOpen);
        Assert.False(hosts.IsLocalOpen);
    }

    [AvaloniaFact]
    public void A_host_opens_its_projects_and_a_session_opens_its_terminal()
    {
        using var phone = new Phone();
        var hosts = (HostsViewModel)phone.Top<HostsPage>().DataContext!;

        hosts.Online[0].OpenCommand.Execute(null);
        Phone.Settle();
        var host = (HostViewModel)phone.Top<HostPage>().DataContext!;
        Assert.Equal("MacBook Pro", host.Title);
        Assert.Equal(["Octopus · ~/code/Octopus", "OctopusServer · ~/code/OctopusServer", "TKCode.Arc · ~/code/TKCode.Arc"], host.Sections.Select(s => s.Header));
        var waiting = host.Sections[0].Sessions[0];
        Assert.Equal("远程协议握手实现", waiting.Title);
        Assert.True(waiting.IsWaiting);
        Assert.Equal("等你", waiting.StatusText);
        Assert.Equal("要运行 dotnet test 吗？", waiting.Preview);
        // Five sessions on this host, each a row.
        Assert.Equal(5, phone.Shell.Top!.GetVisualDescendants().OfType<ListItem>().Count(row => row.DataContext is Octopus.Mobile.Common.SessionRowViewModel));

        waiting.OpenCommand.Execute(null);
        Phone.Settle();
        var terminal = (TerminalViewModel)phone.Top<TerminalPage>().DataContext!;
        Assert.Equal("远程协议握手实现", terminal.Title);
        Assert.Equal("Octopus · MacBook Pro · 局域网", terminal.Location);
        Assert.Equal(3, phone.Shell.Stack!.Depth);

        terminal.BackCommand.Execute(null);
        Phone.Settle();
        phone.Top<HostPage>();
        host.BackCommand.Execute(null);
        Phone.Settle();
        phone.Top<HostsPage>();
    }

    [AvaloniaFact]
    public void Leaving_a_terminal_ends_its_connection_but_covering_it_does_not()
    {
        using var phone = new Phone();
        var session = phone.Workspace.Hosts[0].Projects[0].Sessions[0];
        phone.Shell.ShowHost(session.Project.Host);
        phone.Shell.ShowSession(session);
        Assert.True(Phone.Until(() => phone.Workspace.ConnectionOf(session) is not null));
        var connection = phone.Workspace.ConnectionOf(session)!;

        phone.Shell.Back();
        Assert.True(Phone.Until(() => connection.Exited.IsCompleted));
    }

    [AvaloniaFact]
    public void The_new_sheet_starts_an_agent_or_a_terminal_in_the_chosen_project()
    {
        using var phone = new Phone();
        var mac = phone.Workspace.Hosts[0];
        phone.Shell.ShowHost(mac);
        Phone.Settle();
        var host = (HostViewModel)phone.Top<HostPage>().DataContext!;
        var sheet = host.NewSession;

        host.OpenNewSessionCommand.Execute(null);
        Phone.Settle();
        Assert.True(sheet.IsOpen);
        Assert.Equal(["Claude", "Codex", "终端"], sheet.Kinds);
        Assert.True(sheet.Projects[0].IsSelected);
        Assert.True(sheet.IsAgent);
        Assert.Equal(["Fable 5.2", "Opus 5.5", "Sonnet 5.5"], sheet.Models);
        Assert.Equal("智能体默认", sheet.ModelText);
        // The model sheet lists the default and the agent's models.
        var models = phone.Top<HostPage>().FindControl<ActionSheet>("ModelSheet")!;
        Assert.Equal(4, models.Actions.Count);

        // An agent in the second project, with a model and a prompt.
        sheet.Projects[1].ChooseCommand.Execute(null);
        Assert.False(sheet.Projects[0].IsSelected);
        sheet.SelectedKind = 1;
        Assert.Equal(["GPT-6", "GPT-6 mini"], sheet.Models);
        Assert.Equal(3, models.Actions.Count);
        sheet.SelectModelCommand.Execute("GPT-6 mini");
        sheet.Prompt = "给控制连接加设备签名校验\n先写测试";
        sheet.CreateCommand.Execute(null);
        Phone.Settle();

        Assert.False(sheet.IsOpen);
        var created = mac.Projects[1].Sessions[^1];
        Assert.Equal(SessionKind.Agent, created.Kind);
        Assert.Equal("codex", created.AgentId);
        Assert.Equal("给控制连接加设备签名校验", created.Title);
        Assert.Equal("GPT-6 mini · 高", created.Detail);
        // The new session's terminal is on top, and going back shows it in its project.
        var terminal = (TerminalViewModel)phone.Top<TerminalPage>().DataContext!;
        Assert.Same(created, terminal.Session);
        terminal.BackCommand.Execute(null);
        Phone.Settle();
        Assert.Equal(2, host.Sections[1].Sessions.Count);

        // A plain terminal has no model, effort or prompt.
        host.OpenNewSessionCommand.Execute(null);
        sheet.SelectedKind = 2;
        Assert.False(sheet.IsAgent);
        sheet.CreateCommand.Execute(null);
        Phone.Settle();
        var shell = mac.Projects[0].Sessions[^1];
        Assert.Equal(SessionKind.Terminal, shell.Kind);
        Assert.Null(shell.AgentId);
    }

    [AvaloniaFact]
    public void Pairing_shows_the_number_and_the_host_joins_the_list_once_it_allows()
    {
        // Long enough that the number is seen before the host allows, even on a slow machine.
        using var phone = new Phone(pairingDelay: TimeSpan.FromSeconds(1));
        var hosts = (HostsViewModel)phone.Top<HostsPage>().DataContext!;
        var linux = phone.Workspace.Hosts.Single(host => !host.IsPaired);

        hosts.Unpaired[0].OpenCommand.Execute(null);
        var pairing = (PairingViewModel)phone.Top<PairingPage>().DataContext!;
        Assert.Equal("Linux 服务器", pairing.HostName);
        Assert.True(Phone.Until(() => pairing.HasCode));
        Assert.Equal("482 913", pairing.Code);
        Assert.True(pairing.IsComparing);

        // The made-up host allows after a moment: the page leaves by itself.
        Assert.True(Phone.Until(() => linux.IsPaired && phone.Shell.Top is HostsPage));
        Assert.Empty(hosts.Unpaired);
        Assert.Contains(hosts.Online, row => row.Name == "Linux 服务器");
    }

    [AvaloniaFact]
    public void Cancelling_a_pairing_leaves_the_host_unpaired()
    {
        using var phone = new Phone();
        var linux = phone.Workspace.Hosts.Single(host => !host.IsPaired);
        phone.Shell.ShowPairing(linux);
        var pairing = (PairingViewModel)phone.Top<PairingPage>().DataContext!;

        pairing.CancelCommand.Execute(null);
        Phone.Settle(400);

        phone.Top<HostsPage>();
        Assert.False(linux.IsPaired);
        Assert.False(pairing.HasFailed);
    }

    [AvaloniaFact]
    public void Signed_out_the_first_page_is_the_welcome_page_and_sign_in_goes_to_the_official_service()
    {
        using var phone = new Phone(signedIn: false);
        var page = phone.Top<WelcomePage>();
        var welcome = (WelcomeViewModel)page.DataContext!;
        var signIn = welcome.SignIn;
        Assert.Equal(1, phone.Shell.Stack!.Depth);
        // "Sign in", and the two other ways: nothing to type, no address in sight.
        Assert.True(welcome.IsChoosing);
        Assert.True(welcome.HasOfficialServer);
        Assert.False(welcome.IsOnTop);
        Assert.Null(page.Header);
        Assert.Equal(["LocalRow", "CustomRow"], page.GetVisualDescendants().OfType<ListItem>().Where(row => row.IsVisible).Select(row => row.Name));
        Assert.Empty(page.GetVisualDescendants().OfType<TextField>());

        // "Sign in": the code at once, and the official service's page in the browser.
        welcome.SignInCommand.Execute(null);
        Assert.True(Phone.Until(() => signIn.HasCode));
        Assert.False(welcome.IsChoosing);
        Assert.Equal("WDJB-MJHT", signIn.UserCode);
        Assert.Equal("octopus.example", phone.Opened?.Host);
        phone.Opened = null;
        signIn.BrowseCommand.Execute(null);
        Assert.Equal("octopus.example", phone.Opened?.Host);
        // The official service is not an address the person typed: the custom-service page stays empty.
        Assert.Equal("", phone.Account.Server);

        // The made-up browser confirmation arrives after a moment.
        Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage));
        Assert.True(phone.Account.IsSignedIn);
    }

    [AvaloniaFact]
    public void A_sign_in_that_is_given_up_brings_the_choices_back()
    {
        using var phone = new Phone(signedIn: false, signInDelay: TimeSpan.FromMinutes(1));
        var welcome = (WelcomeViewModel)phone.Top<WelcomePage>().DataContext!;

        welcome.SignInCommand.Execute(null);
        Assert.True(Phone.Until(() => welcome.SignIn.HasCode));
        Assert.False(welcome.SignInCommand.CanExecute(null));
        welcome.SignIn.CancelCommand.Execute(null);

        Assert.True(welcome.IsChoosing);
        Assert.False(welcome.SignIn.HasError);
        Assert.True(welcome.SignInCommand.CanExecute(null));
        Assert.False(phone.Account.IsSignedIn);
    }

    [AvaloniaFact]
    public void Custom_service_asks_for_the_address_and_signs_in_there()
    {
        using var phone = new Phone(signedIn: false);
        var welcome = (WelcomeViewModel)phone.Top<WelcomePage>().DataContext!;

        welcome.UseCustomCommand.Execute(null);
        var page = (CustomServerViewModel)phone.Top<CustomServerPage>().DataContext!;
        Assert.Equal(2, phone.Shell.Stack!.Depth);
        Assert.Equal("", page.Server);
        Assert.True(page.IsEntering);
        Assert.False(page.StartCommand.CanExecute(null));

        page.Server = "my.octopus.test";
        page.StartCommand.Execute(null);
        Assert.True(Phone.Until(() => page.SignIn.HasCode));
        Assert.False(page.IsEntering);
        Assert.Equal("my.octopus.test", phone.Opened?.Host);
        Assert.Equal("my.octopus.test", phone.Account.Server);

        Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage));
        Assert.Equal(1, phone.Shell.Stack!.Depth);
    }

    [AvaloniaFact]
    public void Without_an_official_service_sign_in_opens_the_address_page_and_there_is_no_row_for_it()
    {
        using var phone = new Phone(signedIn: false, official: false);
        var page = phone.Top<WelcomePage>();
        var welcome = (WelcomeViewModel)page.DataContext!;
        Assert.False(welcome.HasOfficialServer);
        Assert.Equal(["LocalRow"], page.GetVisualDescendants().OfType<ListItem>().Where(row => row.IsVisible).Select(row => row.Name));

        welcome.SignInCommand.Execute(null);

        phone.Top<CustomServerPage>();
        Assert.False(welcome.SignIn.HasCode);
        Assert.Null(phone.Opened);
    }

    [AvaloniaFact]
    public void Without_an_account_the_list_says_so_and_offers_to_sign_in_to_leave_and_to_add_a_computer()
    {
        using var phone = new Phone(localOnly: true, pairingDelay: TimeSpan.FromSeconds(1));
        var page = phone.Top<HostsPage>();
        var hosts = (HostsViewModel)page.DataContext!;
        Assert.Equal("未登录", hosts.AccountName);
        Assert.True(hosts.IsSignedOut);
        // Only what the phone paired with itself: reached directly, or offline. No group of the account's other computers.
        Assert.Equal(["MacBook Pro"], hosts.Online.Select(row => row.Name));
        Assert.Equal(["Mac mini"], hosts.Offline.Select(row => row.Name));
        Assert.Empty(hosts.Unpaired);
        Assert.StartsWith("局域网", hosts.Online[0].Summary);
        Assert.True(page.FindControl<Control>("SignedOutButton")!.IsVisible);
        Assert.False(page.FindControl<Control>("AccountButton")!.IsVisible);

        // The entry opens "Sign in" and "Leave local network mode", not "Sign out".
        hosts.OpenAccountCommand.Execute(null);
        Assert.True(hosts.IsLocalOpen);
        Assert.False(hosts.IsAccountOpen);
        Assert.Equal(2, page.FindControl<ActionSheet>("LocalSheet")!.Actions.Count);
        hosts.IsLocalOpen = false;

        // "Add a computer": the address, the number on the page every pairing has, and the list again with one more.
        hosts.AddHostCommand.Execute(null);
        var add = (AddHostViewModel)phone.Top<AddHostPage>().DataContext!;
        add.Target = "192.168.1.20:7420";
        add.ConnectCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is PairingPage));
        var pairing = (PairingViewModel)phone.Top<PairingPage>().DataContext!;
        Assert.True(Phone.Until(() => pairing.HasCode));
        Assert.Equal(("192.168.1.20:7420", "482 913"), (pairing.HostName, pairing.Code));
        Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage));
        Assert.Same(page, phone.Shell.Top);
        Assert.Contains(hosts.Online, row => row.Name == "192.168.1.20");

        // Signing in from here is the welcome page's "Sign in", on top of the list.
        hosts.SignInCommand.Execute(null);
        var welcome = (WelcomeViewModel)phone.Top<WelcomePage>().DataContext!;
        Assert.True(welcome.IsOnTop);
        Assert.NotNull(phone.Top<WelcomePage>().Header);
        Assert.False(welcome.ShowsLocal);
        welcome.BackCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage));

        // Leaving: the welcome page is the first page again.
        hosts.LeaveLocalCommand.Execute(null);
        Phone.Pump();
        phone.Top<WelcomePage>();
        Assert.Equal(1, phone.Shell.Stack!.Depth);
        Assert.False(phone.Account.IsLocalOnly);
    }

    [AvaloniaFact]
    public void An_empty_list_says_what_is_missing_and_offers_to_add_a_computer()
    {
        using var phone = new Phone(localOnly: true);
        phone.Workspace.Hosts.Clear();
        Phone.Pump();
        var page = phone.Top<HostsPage>();
        var hosts = (HostsViewModel)page.DataContext!;

        Assert.True(hosts.IsEmpty);
        Assert.Equal("还没有添加电脑。", hosts.EmptyText);
        Assert.True(page.FindControl<Control>("AddHostButton")!.IsEffectivelyVisible);
        // One way to add, not two: the row under the list is for a list that has something in it.
        Assert.False(page.FindControl<Control>("AddGroup")!.IsVisible);

        // Signed in, the same place says that nothing of the account has remote access on, and still offers to add one.
        using var signedIn = new Phone();
        signedIn.Workspace.Hosts.Clear();
        Phone.Pump();
        var account = (HostsViewModel)signedIn.Top<HostsPage>().DataContext!;
        Assert.Equal("这个账号下还没有开启远程访问的电脑。", account.EmptyText);
        Assert.True(signedIn.Top<HostsPage>().FindControl<Control>("AddHostButton")!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void Signing_out_closes_every_page()
    {
        // The made-up account confirms a new sign-in by itself: slowly here, so the welcome page is still there to see.
        using var phone = new Phone(signInDelay: TimeSpan.FromMinutes(1));
        var session = phone.Workspace.Hosts[0].Projects[0].Sessions[0];
        phone.Shell.ShowHost(session.Project.Host);
        phone.Shell.ShowSession(session);
        Assert.True(Phone.Until(() => phone.Workspace.ConnectionOf(session) is not null));
        var connection = phone.Workspace.ConnectionOf(session)!;

        phone.Account.SignOut();
        Phone.Pump();

        phone.Top<WelcomePage>();
        Assert.Equal(1, phone.Shell.Stack!.Depth);
        Assert.True(Phone.Until(() => connection.Exited.IsCompleted));
    }
}
