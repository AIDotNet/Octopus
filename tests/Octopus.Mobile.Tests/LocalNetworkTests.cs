using Avalonia.Headless.XUnit;
using Octopus.Mobile.Account;
using Octopus.Mobile.Hosts;
using Octopus.Remote.Security;
using Octopus.Remote.Tests;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>
/// The phone without an account (~/code/Octopus/design/mobile/01-登录与连接方式.md §7): the welcome page's three ways,
/// and a computer on the local network added by its address or its pairing link, with no service taking part. The
/// computer is a real host service that listens on the loopback network; the relay is the stand-in, there to be signed
/// in at or to count that nothing asked it for anything.
/// </summary>
public class LocalNetworkTests
{
    private static readonly TimeSpan Open = TimeSpan.FromMinutes(1);

    [AvaloniaFact]
    public async Task Without_an_official_service_sign_in_asks_for_an_address_and_remembers_it()
    {
        await using var relay = new FakeRelay();
        string folder;
        using (var phone = new RealPhone(null))
        {
            folder = phone.Folder;
            phone.Top<WelcomePage>();
            var welcome = phone.Model<WelcomeViewModel>();
            // "Sign in" leads to the address anyway: no row of its own for that.
            Assert.False(welcome.HasOfficialServer);
            Assert.False(welcome.ShowsCustom);
            Assert.True(welcome.ShowsLocal);

            welcome.SignInCommand.Execute(null);
            phone.Top<CustomServerPage>();
            var page = phone.Model<CustomServerViewModel>();
            Assert.Equal("", page.Server);
            Assert.False(welcome.SignIn.HasCode);
            page.Server = relay.Url;
            page.StartCommand.Execute(null);
            Assert.True(Phone.Until(() => page.SignIn.HasCode), "the page shows a code: " + page.SignIn.ErrorText);
            Assert.Equal($"{relay.Url}/device?code={page.SignIn.UserCode}", phone.Opened!.ToString());
            Assert.True(relay.Approve(page.SignIn.UserCode!));
            Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage), "the host list replaces the sign-in pages");
            Assert.Equal("Ada", phone.Model<HostsViewModel>().AccountName);
        }

        // The app starts again, signed in. Signed out, the address typed last time is where the page starts.
        using (var phone = new RealPhone(null, folder))
        {
            phone.Model<HostsViewModel>().SignOutCommand.Execute(null);
            Assert.True(Phone.Until(() => phone.Shell.Top is WelcomePage));
            phone.Model<WelcomeViewModel>().SignInCommand.Execute(null);
            var page = phone.Model<CustomServerViewModel>();
            Assert.Equal(relay.Url, page.Server);
            Assert.True(page.StartCommand.CanExecute(null));
            // Back: the welcome page again.
            page.BackCommand.Execute(null);
            Assert.True(Phone.Until(() => phone.Shell.Top is WelcomePage));
        }
    }

    [AvaloniaFact]
    public async Task Without_an_account_a_computer_is_added_by_its_address_and_no_service_is_asked()
    {
        await using var relay = new FakeRelay();
        await using var den = Computer.StartLocal();
        den.Workspace.AddProject("Octopus", "~/code/Octopus");
        using var phone = new RealPhone(relay);

        // Nothing paired yet: "Local network" is the page where the first computer is added.
        phone.Model<WelcomeViewModel>().UseLocalCommand.Execute(null);
        phone.Top<AddHostPage>();
        var add = phone.Model<AddHostViewModel>();
        Assert.False(add.ConnectCommand.CanExecute(null));
        Assert.False(phone.Remote.Account.IsLocalOnly);

        den.Service.OpenPairing(Open);
        add.Target = "  " + den.Address + " ";
        add.ConnectCommand.Execute(null);
        Assert.True(add.IsConnecting);
        // The computer answered: both sides show one number, on the page every pairing has.
        Assert.True(Phone.Until(() => phone.Shell.Top is PairingPage && phone.Shell.Top.DataContext is PairingViewModel { HasCode: true }), "the number is shown: " + add.ErrorText);
        var pairing = phone.Model<PairingViewModel>();
        var request = Assert.Single(den.Service.PairingRequests);
        Assert.Equal(request.Sas, pairing.Code!.Replace(" ", ""));
        Assert.Equal(den.Address, pairing.HostName);
        Assert.Equal("Ada 的 iPhone", request.Device.Name);
        Assert.True(pairing.IsComparing);
        Assert.Empty(phone.Remote.Connections.Hosts.Peers);

        // Allowed on the computer: the phone is used without an account from now on, and its list has the computer,
        // connected directly.
        request.Allow();
        Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage), "the host list replaces the welcome page");
        Assert.True(phone.Remote.Account.IsLocalOnly);
        Assert.False(phone.Remote.Account.IsSignedIn);
        Assert.Equal(1, phone.Shell.Stack!.Depth);
        var hosts = phone.Model<HostsViewModel>();
        Assert.Equal("未登录", hosts.AccountName);
        Assert.True(Phone.Until(() => hosts.Online.Count == 1 && hosts.Online[0].Host.Projects.Count == 1), "the computer is connected and shows its project");
        Assert.Equal(("Den", "局域网 · 1 个项目"), (hosts.Online[0].Name, hosts.Online[0].Summary));
        Assert.Empty(hosts.Unpaired);
        Assert.Empty(hosts.Offline);
        Assert.Equal(DeviceKind.Phone, Assert.Single(den.Service.Devices.Peers).Kind);
        // The computer is remembered with its address: that is how it is reached next time.
        var peer = Assert.Single(phone.Remote.Connections.Hosts.Peers);
        Assert.Equal(den.Identity.Id, peer.Id);
        Assert.Contains(peer.Addresses, address => address.Contains(den.Address, StringComparison.Ordinal));

        // Looking at the list again asks the computers, not the service.
        await phone.Remote.Workspace.RefreshAsync();
        Phone.Settle(300);
        Assert.Equal(0, relay.Requests);
    }

    [AvaloniaFact]
    public async Task A_pairing_link_pairs_without_a_number()
    {
        await using var relay = new FakeRelay();
        await using var den = Computer.StartLocal();
        using var phone = new RealPhone(relay);
        phone.Model<WelcomeViewModel>().UseLocalCommand.Execute(null);
        var add = phone.Model<AddHostViewModel>();

        den.Service.OpenPairing(Open);
        var link = den.Service.CreatePairingLink([$"ws://{den.Address}"])!;
        add.Target = link.ToString();
        add.ConnectCommand.Execute(null);

        // Straight to the list: the link's token stood for the number, nobody at the computer was asked.
        Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage), "the computer is paired: " + add.ErrorText);
        Assert.Empty(den.Service.PairingRequests);
        Assert.Single(den.Service.Devices.Peers);
        var hosts = phone.Model<HostsViewModel>();
        Assert.True(Phone.Until(() => hosts.Online.Count == 1), "the computer is connected");
        Assert.Equal("Den", hosts.Online[0].Name);
        Assert.StartsWith("局域网", hosts.Online[0].Summary);
        Assert.True(phone.Remote.Account.IsLocalOnly);
        Assert.Equal(0, relay.Requests);
    }

    [AvaloniaFact]
    public async Task What_cannot_be_added_says_why_and_the_page_stays_to_try_again()
    {
        await using var den = Computer.StartLocal();
        using var phone = new RealPhone(null);
        phone.Model<WelcomeViewModel>().UseLocalCommand.Execute(null);
        var add = phone.Model<AddHostViewModel>();

        void Try(string target)
        {
            add.Target = target;
            add.ConnectCommand.Execute(null);
        }

        // Neither an address nor a link.
        Try("two words");
        Assert.True(Phone.Until(() => add.HasError && !add.IsConnecting));
        Assert.Equal("这不是一个地址，也不是配对链接。", add.ErrorText);

        // A computer that is not accepting new devices: where to turn that on.
        Try(den.Address);
        Assert.False(add.HasError);
        Assert.True(Phone.Until(() => add.HasError && !add.IsConnecting));
        Assert.Contains("开始配对", add.ErrorText);

        // An address nothing answers at.
        Try("127.0.0.1:9");
        Assert.True(Phone.Until(() => add.HasError && !add.IsConnecting, 20_000));
        Assert.StartsWith("连不上那台电脑。", add.ErrorText);
        phone.Top<AddHostPage>();

        // Refused at the computer, after the number was shown: said where the number is, and where it can be tried again.
        den.Service.OpenPairing(Open);
        Try(den.Address);
        Assert.True(Phone.Until(() => phone.Shell.Top is PairingPage && den.Service.PairingRequests.Count == 1));
        var pairing = phone.Model<PairingViewModel>();
        den.Service.PairingRequests[0].Refuse();
        Assert.True(Phone.Until(() => pairing.HasFailed && add.HasError));
        Assert.Equal("主机没有允许这台手机。", pairing.FailureText);
        Assert.Equal("主机没有允许这台手机。", add.ErrorText);
        pairing.CancelCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is AddHostPage));
        Assert.True(add.ConnectCommand.CanExecute(null));

        // Nothing was paired, and the phone has not become one that is used without an account.
        Assert.Empty(phone.Remote.Connections.Hosts.Peers);
        Assert.False(phone.Remote.Account.IsLocalOnly);
        add.BackCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is WelcomePage));
    }

    [AvaloniaFact]
    public async Task Used_without_an_account_the_app_starts_at_its_list_until_that_is_left_and_the_pairing_stays()
    {
        await using var relay = new FakeRelay();
        await using var den = Computer.StartLocal();
        den.Workspace.AddProject("Octopus", "~/code/Octopus");
        string folder;
        using (var phone = new RealPhone(relay))
        {
            folder = phone.Folder;
            phone.Model<WelcomeViewModel>().UseLocalCommand.Execute(null);
            den.Service.OpenPairing(Open);
            phone.Add(den, den.Address);
            den.Service.ClosePairing();
        }
        Assert.True(Phone.Until(() => den.Service.Connections.Count == 0), "the phone that was closed is gone from the computer");

        // The app starts again: no welcome page, and the computer is connected to by itself, at its address.
        using (var phone = new RealPhone(relay, folder))
        {
            var hosts = phone.Model<HostsViewModel>();
            phone.Top<HostsPage>();
            Assert.Equal("未登录", hosts.AccountName);
            Assert.True(Phone.Until(() => hosts.Online.Count == 1 && hosts.Online[0].Host.Projects.Count == 1), "the paired computer is back with its projects");
            Assert.Empty(den.Service.PairingRequests);

            // The account entry offers to sign in or to leave. Leaving: the welcome page, no connection left open,
            // nothing on screen; but the computer stays paired.
            hosts.OpenAccountCommand.Execute(null);
            Assert.True(hosts.IsLocalOpen);
            Assert.False(hosts.IsAccountOpen);
            hosts.LeaveLocalCommand.Execute(null);
            Assert.True(Phone.Until(() => phone.Shell.Top is WelcomePage), "the welcome page is back");
            Assert.False(phone.Remote.Account.IsLocalOnly);
            Assert.True(Phone.Until(() => den.Service.Connections.Count == 0), "the connection to the computer ended");
            Assert.Empty(phone.Remote.Workspace.Hosts);
            Assert.Equal(den.Identity.Id, Assert.Single(phone.Remote.Connections.Hosts.Peers).Id);

            // "Local network" again: there is a computer already, so its list rather than the page to add one.
            phone.Model<WelcomeViewModel>().UseLocalCommand.Execute(null);
            Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage));
            var again = phone.Model<HostsViewModel>();
            Assert.True(Phone.Until(() => again.Online.Count == 1), "the computer is connected again without pairing");
            Assert.Empty(den.Service.PairingRequests);
        }

        // And the welcome page is not where the next start begins either. The service never heard of this phone.
        using (var phone = new RealPhone(relay, folder)) phone.Top<HostsPage>();
        Assert.Equal(0, relay.Requests);
    }

    [AvaloniaFact]
    public async Task Signing_in_from_the_local_list_adds_the_accounts_computers_and_keeps_the_added_one()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        await using var den = Computer.StartLocal();
        using var phone = new RealPhone(relay);
        phone.Model<WelcomeViewModel>().UseLocalCommand.Execute(null);
        den.Service.OpenPairing(Open);
        phone.Add(den, den.Service.CreatePairingLink([$"ws://{den.Address}"])!.ToString());
        var local = phone.Model<HostsViewModel>();
        // Without an account nothing of the account is there, online at the relay or not.
        Assert.Equal(["Den"], phone.Remote.Workspace.Hosts.Select(host => host.Name));

        // "Sign in" from the list: the welcome page's sign-in on top of it, with a way back and without "Local network".
        local.OpenAccountCommand.Execute(null);
        local.SignInCommand.Execute(null);
        Assert.False(local.IsLocalOpen);
        var welcome = phone.Model<WelcomeViewModel>();
        Assert.True(welcome.IsOnTop);
        Assert.False(welcome.ShowsLocal);
        Assert.Equal(2, phone.Shell.Stack!.Depth);
        welcome.BackCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage));

        phone.SignIn(relay);
        var hosts = phone.Model<HostsViewModel>();
        Assert.Equal("Ada", hosts.AccountName);
        Assert.Equal(1, phone.Shell.Stack!.Depth);
        Assert.True(Phone.Until(() => hosts.Unpaired.Count == 1 && hosts.Online.Count == 1), "the account's computer joins the one added by hand");
        Assert.Equal("Studio", hosts.Unpaired[0].Name);
        Assert.Equal("Den", hosts.Online[0].Name);
        Assert.StartsWith("局域网", hosts.Online[0].Summary);

        // Signed out again: back to the list without an account (that choice was never taken back), with the added computer only.
        hosts.SignOutCommand.Execute(null);
        Assert.True(Phone.Until(() => phone.Shell.Top is HostsPage && phone.Shell.Top.DataContext is HostsViewModel { IsSignedOut: true }), "the list without an account is back");
        var after = phone.Model<HostsViewModel>();
        Assert.True(phone.Remote.Account.IsLocalOnly);
        Assert.True(Phone.Until(() => after.Online.Count == 1 && after.Unpaired.Count == 0), "only the added computer is left, connected");
        Assert.Equal("Den", after.Online[0].Name);
    }

    [AvaloniaFact]
    public async Task Signed_in_a_computer_the_account_does_not_know_is_added_by_hand()
    {
        await using var relay = new FakeRelay();
        await using var studio = await Computer.StartAsync(relay);
        await using var den = Computer.StartLocal();
        using var phone = new RealPhone(relay);
        phone.SignIn(relay);
        var hosts = phone.Model<HostsViewModel>();
        Assert.True(Phone.Until(() => hosts.Unpaired.Count == 1));

        hosts.AddHostCommand.Execute(null);
        phone.Top<AddHostPage>();
        den.Service.OpenPairing(Open);
        phone.Add(den, den.Address);

        // Back at the same list: the account's computer as before, and the one it does not know, connected directly.
        Assert.Same(hosts, phone.Model<HostsViewModel>());
        Assert.Equal(1, phone.Shell.Stack!.Depth);
        Assert.Equal("Ada", hosts.AccountName);
        Assert.False(phone.Remote.Account.IsLocalOnly);
        Assert.Equal("Den", Assert.Single(hosts.Online).Name);
        Assert.StartsWith("局域网", hosts.Online[0].Summary);
        Assert.Equal("Studio", Assert.Single(hosts.Unpaired).Name);
        // It stays there when the list is read again from the service, which has never heard of it.
        await phone.Remote.Workspace.RefreshAsync();
        Phone.Settle(300);
        Assert.Equal("Den", Assert.Single(hosts.Online).Name);
        Assert.Equal("Studio", Assert.Single(hosts.Unpaired).Name);
    }
}
