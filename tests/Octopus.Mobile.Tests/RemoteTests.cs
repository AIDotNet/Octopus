using Avalonia.Headless.XUnit;
using Octopus.Mobile.Account;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Terminals;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Protocol;
using Octopus.Remote.Relay;
using Octopus.Remote.Security;
using Octopus.Remote.Tests;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>
/// The phone on the real protocol (design/12-远程操作.md §9, R6): it signs in at a relay, sees the account's computers,
/// pairs with one, and shows and operates what runs there. The relay and the host are stand-ins written from the
/// protocol's text; everything between them and the pages is the code a phone runs.
/// </summary>
public class RemoteTests
{
    [AvaloniaFact]
    public async Task Signing_in_shows_the_accounts_computers_and_only_those()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        await using var stranger = await Computer.StartAsync(relay, "Someone else's", account: "bob");
        using var phone = new RealPhone(relay);

        // Signed out: the welcome page. "Sign in" goes to the official service (as the head set it) without asking for
        // anything; nothing of anyone's computers is there.
        phone.Top<WelcomePage>();
        var welcome = phone.Model<WelcomeViewModel>();
        var page = welcome.SignIn;
        Assert.True(welcome.HasOfficialServer);
        Assert.True(welcome.IsChoosing);
        Assert.False(page.HasError);
        Assert.Empty(phone.Remote.Workspace.Hosts);

        welcome.SignInCommand.Execute(null);
        Assert.True(Phone.Until(() => page.HasCode), "the page shows a code");
        Assert.Matches("^[B-Z]{4}-[B-Z]{4}$", page.UserCode!);
        // The service's page is opened with the code in it, and the service was told this is a phone, by its name,
        // what it runs on and which version of the app it is.
        Assert.Equal($"{relay.Url}/device?code={page.UserCode}", phone.Opened!.ToString());
        Assert.Contains("Ada 的 iPhone", relay.LoginNames);
        Assert.Contains((RelayClientInfo.Platform, RelayClientInfo.AppVersion), relay.LoginClients);
        Assert.NotNull(RelayClientInfo.AppVersion);
        Assert.DoesNotContain('+', RelayClientInfo.AppVersion);
        Assert.False(welcome.SignInCommand.CanExecute(null));
        Assert.False(welcome.IsChoosing);

        Assert.True(relay.Approve(page.UserCode!));
        Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage), "the host list replaces the sign-in page");
        var hosts = phone.Model<HostsViewModel>();
        Assert.Equal("Ada", hosts.AccountName);
        Assert.True(Phone.Until(() => hosts.Unpaired.Count == 1), "the account's computer is listed");
        // The account's computer, not paired yet; not the other account's, and not this phone itself.
        var row = hosts.Unpaired.Single();
        Assert.Equal(("Studio", "配对", true), (row.Name, row.PillText, row.CanOpen));
        Assert.Empty(hosts.Online);
        Assert.Empty(hosts.Offline);
        // Being signed in to the same account opened nothing on the computer.
        Assert.Empty(studio.Service.Connections);
    }

    [AvaloniaFact]
    public async Task A_sign_in_that_fails_says_why_and_can_be_tried_again()
    {
        await using var relay = new FakeRelay();
        using var phone = new RealPhone(relay);
        // A service of the person's own: the page with the address.
        phone.Model<WelcomeViewModel>().UseCustomCommand.Execute(null);
        phone.Top<CustomServerPage>();
        var page = phone.Model<CustomServerViewModel>();
        var flow = page.SignIn;
        Assert.Equal("", page.Server);
        Assert.False(page.StartCommand.CanExecute(null));

        // Not an address, and an address nothing answers at.
        page.Server = "two words";
        page.StartCommand.Execute(null);
        Assert.True(Phone.Until(() => flow.HasError && !flow.IsStarting));
        Assert.StartsWith("没能开始登录：", flow.ErrorText);
        page.Server = "http://127.0.0.1:9";
        page.StartCommand.Execute(null);
        Assert.True(Phone.Until(() => flow.HasError && !flow.IsStarting));
        Assert.StartsWith("没能开始登录：", flow.ErrorText);
        Assert.True(page.IsEntering);

        // Refused in the browser: back to the address, with the reason.
        page.Server = relay.Url;
        page.StartCommand.Execute(null);
        Assert.True(Phone.Until(() => flow.HasCode));
        Assert.False(flow.HasError);
        Assert.False(page.IsEntering);
        Assert.True(relay.Deny(flow.UserCode!));
        Assert.True(Phone.Until(() => flow.HasError));
        Assert.Equal("登录在浏览器里被拒绝了。", flow.ErrorText);
        Assert.True(page.IsEntering);

        // Given up on the phone: no complaint. Then it works, from this page as from the welcome page.
        page.StartCommand.Execute(null);
        Assert.True(Phone.Until(() => flow.HasCode));
        flow.CancelCommand.Execute(null);
        Assert.True(page.IsEntering);
        Assert.False(flow.HasError);
        page.StartCommand.Execute(null);
        Assert.True(Phone.Until(() => flow.HasCode));
        Assert.True(relay.Approve(flow.UserCode!));
        Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage), "the host list replaces the sign-in pages");
        Assert.Equal(1, phone.Shell.Stack!.Depth);
    }

    [AvaloniaFact]
    public async Task Pairing_shows_the_same_number_as_the_computer_and_needs_it_to_be_allowed_there()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        var hosts = phone.Model<HostsViewModel>();
        Assert.True(Phone.Until(() => hosts.Unpaired.Count == 1));

        // The computer is not accepting new devices: the page says where to turn that on.
        hosts.Unpaired[0].OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is PairingPage));
        var closed = phone.Model<PairingViewModel>();
        Assert.Equal("Studio", closed.HostName);
        Assert.True(Phone.Until(() => closed.HasFailed));
        Assert.Contains("开始配对", closed.FailureText);
        Assert.False(closed.HasCode);
        closed.CancelCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage));

        // Pairing is open: both sides show one number, and nothing is paired until the computer says so.
        studio.Service.OpenPairing(TimeSpan.FromMinutes(1));
        hosts.Unpaired[0].OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is PairingPage && phone.Shell.Top.DataContext is PairingViewModel { HasCode: true }), "the number is shown");
        var pairing = phone.Model<PairingViewModel>();
        var request = Assert.Single(studio.Service.PairingRequests);
        Assert.Equal(request.Sas, pairing.Code!.Replace(" ", ""));
        Assert.Matches("^[0-9]{3} [0-9]{3}$", pairing.Code);
        Assert.Equal("Ada 的 iPhone", request.Device.Name);
        Assert.True(pairing.IsComparing);
        Assert.Empty(studio.Service.Devices.Peers);

        // Refused on the computer.
        request.Refuse();
        Assert.True(Phone.Until(() => pairing.HasFailed));
        Assert.Equal("主机没有允许这台手机。", pairing.FailureText);
        Assert.Empty(phone.Remote.Connections.Hosts.Peers);
        pairing.CancelCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage));

        // Allowed: the phone is back at the list, the computer is paired, connected, and a phone to the computer.
        hosts.Unpaired[0].OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => studio.Service.PairingRequests.Count == 1));
        studio.Service.PairingRequests[0].Allow();
        Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage && hosts.Online.Count == 1), "the computer is paired and connected");
        Assert.Empty(hosts.Unpaired);
        Assert.Equal(DeviceKind.Phone, Assert.Single(studio.Service.Devices.Peers).Kind);
        Assert.Equal(studio.Identity.Id, Assert.Single(phone.Remote.Connections.Hosts.Peers).Id);
        Assert.StartsWith("中转", hosts.Online[0].Summary);
    }

    [AvaloniaFact]
    public async Task A_paired_computer_shows_its_projects_and_what_its_sessions_are_doing()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        var octopus = studio.Workspace.AddProject("Octopus", "~/code/Octopus");
        var server = studio.Workspace.AddProject("OctopusServer", "~/code/OctopusServer");
        var handshake = studio.Workspace.AddItem(octopus, "远程协议握手实现", "claude", ItemActivities.Working, "fable-5", "high");
        studio.Workspace.AddItem(octopus, "dotnet watch");
        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        phone.Pair(studio);
        var hosts = phone.Model<HostsViewModel>();
        var row = hosts.Online.Single();

        // The row counts what works on the computer.
        Assert.True(Phone.Until(() => row.PillText == "1 工作中"), "the row says one session works: " + row.PillText + " / " + row.Summary);
        Assert.Equal("中转 · 2 个项目", row.Summary);

        row.OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostPage));
        var host = phone.Model<HostViewModel>();
        Assert.Equal(["Octopus · ~/code/Octopus", "OctopusServer · ~/code/OctopusServer"], host.Sections.Select(section => section.Header));
        var sessions = host.Sections[0].Sessions;
        Assert.Equal(["远程协议握手实现", "dotnet watch"], sessions.Select(session => session.Title));
        // Model and effort as the computer labels them, not its ids.
        Assert.Equal("Fable 5.2 · 高", host.Host.Projects[0].Sessions[0].Detail);
        Assert.Equal(SessionKind.Terminal, host.Host.Projects[0].Sessions[1].Kind);

        // The agent stops to ask: the row says so at once, here and on the first page.
        studio.Workspace.SetActivity(handshake, ItemActivities.Waiting, "要运行 dotnet test 吗？");
        Assert.True(Phone.Until(() => host.Host.Projects[0].Sessions[0].Activity == SessionActivity.Waiting), "the session waits");
        Assert.Equal("要运行 dotnet test 吗？", host.Host.Projects[0].Sessions[0].Title);
        Assert.Equal("1 等你", row.PillText);

        // Something is added on the computer itself: it shows up without asking.
        studio.Workspace.AddItem(server, "部署");
        Assert.True(Phone.Until(() => host.Sections.Count == 2 && host.Sections[1].Sessions.Count == 1), "the new session is listed");

        // Started from the phone: the computer is told the agent's ids, and the new session is the computer's.
        Assert.Single(host.Host.Agents);
        host.OpenNewSessionCommand.Execute(null);
        var sheet = host.NewSession;
        Assert.True(sheet.IsOpen);
        Assert.Equal(["Claude", "终端"], sheet.Kinds);
        Assert.Equal(["Fable 5.2", "Opus 5.5"], sheet.Models);
        sheet.SelectModelCommand.Execute("Opus 5.5");
        sheet.SelectedEffort = 0;
        sheet.Prompt = "把握手规范审一遍";
        sheet.CreateCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is TerminalPage), "the new session's terminal opens: " + sheet.ErrorText);
        Assert.True(studio.Workspace.Created.TryDequeue(out var created));
        Assert.Equal((octopus.Id, ItemKinds.Agent, "claude", "opus-5", "low", "把握手规范审一遍"), (created.ProjectId, created.Kind, created.AgentId, created.Model, created.Effort, created.Prompt));
        var opened = phone.Model<TerminalViewModel>().Session;
        Assert.Equal(("把握手规范审一遍", "Opus 5.5 · 低", SessionActivity.Working), (opened.Title, opened.Detail, opened.Activity));
        // Once in the project, not twice (the computer also announced it).
        Assert.True(Phone.Until(() => host.Host.Projects[0].Sessions.Count == 3));
        Assert.Single(host.Host.Projects[0].Sessions, session => session.Id == opened.Id);
    }

    [AvaloniaFact]
    public async Task Sessions_are_managed_and_projects_made_from_the_phone()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        var octopus = studio.Workspace.AddProject("Octopus", "~/code/Octopus");
        var watch = studio.Workspace.AddItem(octopus, "dotnet watch");
        var gui = studio.Workspace.AddItem(octopus, "接口文档", "claude", surface: ItemSurfaces.Gui);
        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        phone.Pair(studio);
        var row = phone.Model<HostsViewModel>().Online.Single();
        row.OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostPage));
        var host = phone.Model<HostViewModel>();
        Assert.True(Phone.Until(() => host.Host.Projects.Count == 1 && host.Host.Projects[0].Sessions.Count == 2));
        var sessions = host.Host.Projects[0].Sessions;
        var shell = sessions.Single(session => session.Id == watch.Id);
        // How the computer runs it decides how it opens: a GUI conversation has no terminal here.
        Assert.False(shell.IsGui);
        Assert.True(sessions.Single(session => session.Id == gui.Id).IsGui);

        // Rename and restart go to the computer by the item's id.
        var actions = host.Actions;
        actions.ShowMenu(shell, includeOpen: true);
        actions.BeginRenameCommand.Execute(null);
        Assert.Equal("dotnet watch", actions.RenameText);
        actions.RenameText = "  watch 前端  ";
        await actions.ConfirmRenameCommand.ExecuteAsync(null);
        Assert.True(studio.Workspace.Renamed.TryDequeue(out var renamed));
        Assert.Equal((watch.Id, "watch 前端"), renamed);
        Assert.Equal("watch 前端", shell.Title);
        Assert.False(actions.IsRenameOpen);

        await actions.RestartCommand.ExecuteAsync(null);
        Assert.True(studio.Workspace.Restarted.TryDequeue(out var restarted));
        Assert.Equal(watch.Id, restarted);

        // Closed: gone on the computer and here.
        actions.BeginCloseCommand.Execute(null);
        Assert.True(actions.IsCloseOpen);
        await actions.ConfirmCloseCommand.ExecuteAsync(null);
        Assert.True(Phone.Until(() => sessions.All(session => session.Id != watch.Id)), "the closed session leaves the list");
        Assert.True(Phone.Until(() => host.Sections[0].Sessions.Count == 1));

        // A new project: browse the computer's home, step into a folder, make it a project.
        host.NewProjectCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is FolderPickerPage));
        var picker = phone.Model<FolderPickerViewModel>();
        Assert.True(Phone.Until(() => picker.Folders.Count == 2), "the home folder is listed: " + picker.ErrorText);
        Assert.Equal("/home/ada", picker.Path);
        Assert.Equal(["code", "notes"], picker.Folders.Select(folder => folder.Name));
        picker.Folders[0].OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => picker.Path == "/home/ada/code" && !picker.IsLoading));
        Assert.True(picker.IsEmpty);
        Assert.Equal(["/", "home", "ada", "code"], picker.Crumbs.Select(crumb => crumb.Name));
        Assert.Equal("把“code”设为项目", picker.ChooseText);
        await picker.ChooseCommand.ExecuteAsync(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostPage));
        Assert.True(Phone.Until(() => host.Sections.Count == 2), "the new project is listed");
        Assert.Contains(host.Host.Projects, project => project.Path == "/home/ada/code");
        // Made from the "+": the "New" sheet opens on it.
        Assert.True(host.NewSession.IsOpen);
        Assert.Equal("/home/ada/code", host.NewSession.Projects.Single(choice => choice.IsSelected).Path);
    }

    [AvaloniaFact]
    public async Task A_session_that_cannot_be_started_leaves_the_sheet_open_with_the_reason()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        studio.Workspace.AddProject("Octopus", "~/code/Octopus");
        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        phone.Pair(studio);
        var row = phone.Model<HostsViewModel>().Online.Single();
        Assert.True(Phone.Until(() => row.Host.Projects.Count == 1 && row.Host.Agents.Count == 1));
        row.OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostPage));
        var sheet = phone.Model<HostViewModel>().NewSession;
        sheet.Open();

        sheet.Prompt = "fail";
        sheet.CreateCommand.Execute(null);

        Assert.True(Phone.Until(() => sheet.HasError && !sheet.IsBusy));
        Assert.Equal("The agent is not installed on this computer.", sheet.ErrorText);
        Assert.True(sheet.IsOpen);
        Assert.Equal("fail", sheet.Prompt);
        phone.Top<HostPage>();
    }

    [AvaloniaFact]
    public async Task A_terminal_is_looked_at_without_taking_its_size_until_the_phone_is_to_type()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        var project = studio.Workspace.AddProject("Octopus", "~/code/Octopus");
        var item = studio.Workspace.AddItem(project, "dotnet watch");
        var terminal = studio.Workspace.TerminalOf(item.Id);
        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        phone.Pair(studio);
        var host = phone.Model<HostsViewModel>().Online.Single().Host;
        Assert.True(Phone.Until(() => host.Projects.Count == 1 && host.Projects[0].Sessions.Count == 1));
        var session = host.Projects[0].Sessions[0];

        phone.Shell.ShowHost(host);
        phone.Shell.ShowSession(session);
        var page = phone.Top<TerminalPage>();
        var model = phone.Model<TerminalViewModel>();

        // What the computer's terminal shows, and what it prints from now on.
        Assert.True(Phone.Until(() => page.ReadScreenText().Contains($"terminal of {item.Id}", StringComparison.Ordinal)), "the terminal shows the computer's screen: " + page.ReadScreenText());
        terminal.Print("building...\r\n");
        Assert.True(Phone.Until(() => page.ReadScreenText().Contains("building...", StringComparison.Ordinal)));
        // Only looking: the computer's own window still decides the size, and the page says whose it is.
        Assert.True(model.IsViewing);
        Assert.True(model.IsMirror);
        Assert.Equal(1, terminal.Viewers);
        Assert.False(terminal.IsOwnedByDevice);
        Assert.True(Phone.Until(() => session.Columns == FakeHostWorkspace.OwnColumns));
        Assert.Equal((FakeHostWorkspace.OwnColumns, FakeHostWorkspace.OwnRows, "Studio"), (session.Columns, session.Rows, session.SizeOwner));
        Assert.Equal("132×40 · 尺寸由 Studio 决定", model.SizeText);

        // "Fit to phone": the terminal takes this phone's grid, and typing reaches the program.
        model.FitCommand.Execute(null);
        Assert.True(Phone.Until(() => terminal.IsOwnedByDevice && session.SizeOwner is null, 15_000),
            $"the phone decides the size: owned by device {terminal.IsOwnedByDevice}, size owner {session.SizeOwner ?? "(phone)"}, mode {model.Mode}");
        var grid = page.GridSize!.Value;
        Assert.Equal((grid.Columns, grid.Rows), (terminal.Columns, terminal.Rows));
        Assert.Equal((terminal.Columns, terminal.Rows), (session.Columns, session.Rows));
        Assert.True(terminal.Columns < FakeHostWorkspace.OwnColumns);
        Assert.True(Phone.Until(() => model.IsDirect));
        page.SendText("ls\r");
        Assert.True(Phone.Until(() => terminal.Typed == "ls\r"), "what is typed reaches the computer: " + terminal.Typed);
        // And comes back as the program echoes it.
        Assert.True(Phone.Until(() => page.ReadScreenText().Split('\n').Any(line => line.Trim() == "ls")), "the echo is shown: " + page.ReadScreenText());

        // Given back: the computer's window decides again, at its own size; the phone keeps looking.
        model.ReleaseCommand.Execute(null);
        Assert.True(Phone.Until(() => !terminal.IsOwnedByDevice && session.Columns == FakeHostWorkspace.OwnColumns && model.IsViewing), "the size went back");
        Assert.Equal("Studio", session.SizeOwner);
        terminal.Print("still here\r\n");
        Assert.True(Phone.Until(() => page.ReadScreenText().Contains("still here", StringComparison.Ordinal)));

        // Leaving the page ends the view; the terminal on the computer is untouched.
        model.BackCommand.Execute(null);
        Assert.True(Phone.Until(() => terminal.Viewers == 0), "the view ended");
        Assert.Equal("ls\r", terminal.Typed);
    }

    [AvaloniaFact]
    public async Task Signing_out_closes_everything_and_the_next_start_remembers_the_sign_in_and_the_pairing()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        studio.Workspace.AddProject("Octopus", "~/code/Octopus");
        string folder;
        using (var phone = new RealPhone(relay))
        {
            folder = phone.Folder;
            phone.SignIn(relay);
            phone.Pair(studio);
            Assert.Single(studio.Service.Connections);
        }
        Assert.True(Phone.Until(() => studio.Service.Connections.Count == 0), "the phone that was closed is gone from the computer");

        // The app starts again: no welcome page, the paired computer is connected to by itself.
        using (var phone = new RealPhone(relay, folder))
        {
            var hosts = phone.Model<HostsViewModel>();
            Assert.Equal("Ada", hosts.AccountName);
            Assert.True(Phone.Until(() => hosts.Online.Count == 1 && hosts.Online[0].Host.Projects.Count == 1), "the paired computer is back with its projects");
            Assert.Empty(hosts.Unpaired);

            // Signed out: the welcome page, nothing of the account on screen, no connection left open.
            hosts.SignOutCommand.Execute(null);
            Assert.True(Phone.Until(() => phone.Shell.Top is WelcomePage), "the welcome page is back");
            Assert.True(Phone.Until(() => studio.Service.Connections.Count == 0), "the connection to the computer ended");
            Assert.Empty(phone.Remote.Workspace.Hosts);
            Assert.False(phone.Model<WelcomeViewModel>().SignIn.HasError);
            // Who is paired is the phone's own matter, not the account's: signing in again needs no second pairing.
            phone.SignIn(relay);
            var again = phone.Model<HostsViewModel>();
            Assert.True(Phone.Until(() => again.Online.Count == 1), "the computer is connected again without pairing");
            Assert.Empty(studio.Service.PairingRequests);
        }
    }

    [AvaloniaFact]
    public async Task A_phone_removed_from_the_account_is_back_at_the_welcome_page_and_told_why()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        phone.Pair(studio);
        var identity = await DeviceIdentityStore.LoadOrCreateAsync(null, Path.Combine(phone.Folder, "secrets", DeviceIdentityStore.KeyName));

        // Removed on the account page: no token of this phone works any more.
        relay.SignOut(identity.Id);
        relay.ExpireAccessTokens();
        await phone.Remote.Workspace.RefreshAsync();

        Assert.True(Phone.Until(() => phone.Shell.Top is WelcomePage), "the welcome page is back");
        Assert.Contains("登录已经失效", phone.Model<WelcomeViewModel>().SignIn.ErrorText);
        Assert.True(Phone.Until(() => studio.Service.Connections.Count == 0), "the connection to the computer ended");
    }

    [AvaloniaFact]
    public async Task Custom_server_signs_in_directly_with_a_self_hosted_setup_link_without_opening_browser()
    {
        await using var relay = new FakeRelay { SelfHostedAccessKey = "ork_mobile_self_hosted_01234567" };
        await using var studio = await Computer.StartAsync(relay);
        using var phone = new RealPhone(relay);

        phone.Model<WelcomeViewModel>().UseCustomCommand.Execute(null);
        phone.Top<CustomServerPage>();
        var page = phone.Model<CustomServerViewModel>();

        page.Server = $"octopus://relay?v=1&url={Uri.EscapeDataString(relay.Url)}&key={Uri.EscapeDataString(relay.SelfHostedAccessKey)}";
        Assert.Equal(relay.Url, page.Server);
        Assert.Equal(relay.SelfHostedAccessKey, page.AccessKey);

        page.StartCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage && phone.Remote.Account.IsSignedIn), "signed in directly to host list: " + page.SignIn.ErrorText);
        Assert.Null(phone.Opened); // browser was never opened
        var hosts = phone.Model<HostsViewModel>();
        Assert.True(Phone.Until(() => hosts.Unpaired.Any(row => row.Host.Id == studio.Identity.Id)), "host discovered on self-hosted relay");
    }

    [AvaloniaFact]
    public async Task Background_and_foreground_resume_probes_connections_and_recovers_without_hanging()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        var project = studio.Workspace.AddProject("Octopus", "/home/ada/code/Octopus");
        var gui = studio.Workspace.AddItem(project, "后台恢复测试", "claude", surface: ItemSurfaces.Gui);
        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        phone.Pair(studio);

        var host = phone.Remote.Workspace.Hosts.Single(h => h.Id == studio.Identity.Id);
        Assert.True(Phone.Until(() => host.Projects.Count == 1 && host.Projects[0].Sessions.Count == 1));
        var session = host.Projects[0].Sessions[0];
        phone.Shell.ShowSession(session);
        Assert.True(Phone.Until(() => phone.Shell.Top is Octopus.Mobile.Conversations.ConversationPage));
        var convVm = phone.Model<Octopus.Mobile.Conversations.ConversationViewModel>();
        Assert.True(Phone.Until(() => convVm.IsLoaded));

        var bgCount = 0;
        var fgCount = 0;
        void OnBg() => bgCount++;
        void OnFg() => fgCount++;
        MobileHost.Backgrounded += OnBg;
        MobileHost.Foregrounded += OnFg;
        try
        {
            MobileHost.NotifyBackgrounded();
            phone.Remote.Workspace.OnAppBackgrounded();
            Assert.True(MobileHost.IsBackgrounded);
            Assert.Equal(1, bgCount);

            // Simulate the active client transport dropping while the phone was suspended in the background.
            var dropped = phone.Remote.Connections.ClientOf(host.Id);
            Assert.NotNull(dropped);
            await dropped!.DisposeAsync();

            // Resuming to the foreground wakes reconnect/probe immediately without hanging, and the conversation reattaches cleanly.
            MobileHost.NotifyForegrounded();
            phone.Remote.Workspace.OnAppForegrounded();
            Assert.False(MobileHost.IsBackgrounded);
            Assert.Equal(1, fgCount);

            Assert.True(Phone.Until(() => studio.Service.Connections.Count == 1 && host.IsOnline, 10_000),
                $"host did not recover after foreground resume: link={host.Link}, conns={studio.Service.Connections.Count}");
            Assert.True(Phone.Until(() => convVm.IsLoaded && !convVm.HasError, 10_000),
                $"conversation did not stay healthy after resume: error={convVm.ErrorText}");
        }
        finally
        {
            MobileHost.Backgrounded -= OnBg;
            MobileHost.Foregrounded -= OnFg;
            MobileHost.NotifyForegrounded();
        }
    }
}
