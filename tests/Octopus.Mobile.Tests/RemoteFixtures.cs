using System.Net;
using Avalonia.Controls;
using Octopus.Mobile.Account;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Remote;
using Octopus.Mobile.Shell;
using Octopus.Remote.Host;
using Octopus.Remote.Protocol;
using Octopus.Remote.Relay;
using Octopus.Remote.Security;
using Octopus.Remote.Tests;
using Xunit;

namespace Octopus.Mobile.Tests;

/// <summary>
/// A computer running Octopus: a host service with a made-up workspace. Either one of an account, signed in and
/// registered at the relay, or one that only listens on the local network and knows no service.
/// </summary>
internal sealed class Computer : IAsyncDisposable
{
    public DeviceIdentity Identity { get; }
    public FakeHostWorkspace Workspace { get; } = new();
    public RemoteHostService Service { get; }
    /// <summary>The computer's registration at the relay; null for one without an account.</summary>
    public RelayHostLink? Link { get; }

    private Computer(DeviceIdentity identity, string name, FakeRelay? relay = null, RelayTokens? tokens = null,
        bool advertiseWorkspaces = true, bool advertiseFiles = true, bool advertiseGit = true, bool advertiseAcpGui = true)
    {
        Identity = identity;
        List<string> capabilities = advertiseWorkspaces
            ? [RemoteMethods.CapabilityWorkspace, RemoteMethods.CapabilitySizeRelease, RemoteMethods.CapabilityWorkspaces, RemoteMethods.CapabilityBrowse]
            : [RemoteMethods.CapabilityWorkspace, RemoteMethods.CapabilitySizeRelease];
        if (advertiseFiles) capabilities.Add(RemoteMethods.CapabilityFiles);
        if (advertiseGit) capabilities.Add(RemoteMethods.CapabilityGit);
        if (advertiseAcpGui) capabilities.Add(RemoteMethods.CapabilityAcpGui);
        Service = new RemoteHostService(identity, () => new DeviceDescription(name, DeviceKind.Desktop), new PairedPeerStore(null), Workspace,
            new HelloMessage { AppVersion = "1.0", Capabilities = capabilities });
        if (relay is null || tokens is null) return;
        Link = new RelayHostLink(new RelayEndpoint(new Uri(relay.Url), tokens.AccessToken), identity, () => name, transport => Service.ServeAsync(transport));
        Link.Start();
    }

    /// <summary>Where the computer listens on the local network (the loopback one here), as a person would type it.</summary>
    public string Address => $"127.0.0.1:{Service.LanPort}";

    /// <summary>A computer of an account, reached through the relay.</summary>
    public static async Task<Computer> StartAsync(FakeRelay relay, string name = "Studio", string account = "ada",
        bool advertiseWorkspaces = true, bool advertiseFiles = true, bool advertiseGit = true, bool advertiseAcpGui = true)
    {
        var identity = DeviceIdentity.Create();
        var url = new Uri(relay.Url);
        var login = await RelayAccountClient.StartLoginAsync(url, identity, name);
        Assert.True(relay.Approve(login.UserCode, account));
        var (_, tokens) = await RelayAccountClient.PollLoginAsync(url, login, identity);
        var computer = new Computer(identity, name, relay, tokens!, advertiseWorkspaces, advertiseFiles, advertiseGit, advertiseAcpGui);
        Assert.True(Phone.Until(() => computer.Link!.State == RelayLinkState.Online), "the computer registers at the relay: " + computer.Link!.Error);
        return computer;
    }

    /// <summary>A computer nobody signed in on: remote access is on, and it listens on the network.</summary>
    public static Computer StartLocal(string name = "Den", bool advertiseFiles = true, bool advertiseGit = true, bool advertiseAcpGui = true)
    {
        var computer = new Computer(DeviceIdentity.Create(), name, advertiseFiles: advertiseFiles, advertiseGit: advertiseGit, advertiseAcpGui: advertiseAcpGui);
        computer.Service.StartLan(0, IPAddress.Loopback);
        return computer;
    }

    public async ValueTask DisposeAsync()
    {
        if (Link is not null) await Link.DisposeAsync();
        await Service.DisposeAsync();
        Identity.Dispose();
    }
}

/// <summary>A phone with nothing made up: its own folder, key, sign-in and paired computers, and the app's pages on top.</summary>
internal sealed class RealPhone : IDisposable
{
    /// <param name="relay">The official service of this phone's build; null for a build that has none.</param>
    public RealPhone(FakeRelay? relay, string? folder = null)
    {
        Folder = folder ?? Directory.CreateTempSubdirectory("octopus-phone-").FullName;
        Remote = new MobileRemote(new FileMobileStore(Folder), "Ada 的 iPhone", relay?.Url ?? "");
        Remote.Session.PollInterval = TimeSpan.FromMilliseconds(30);
        Remote.Load();
        Shell = new MobileShell(Remote.Workspace, Remote.Account, uri => Opened = uri);
        Window = new Window { Width = MobileApp.PreviewWidth, Height = MobileApp.PreviewHeight, Content = Shell };
        Window.Show();
        Phone.Pump();
    }

    public string Folder { get; }
    public MobileRemote Remote { get; }
    public MobileShell Shell { get; }
    public Window Window { get; }
    public Uri? Opened { get; private set; }

    public T Top<T>() where T : class => Assert.IsType<T>(Shell.Top);
    public T Model<T>() where T : class => Assert.IsType<T>(Shell.Top!.DataContext);

    /// <summary>
    /// Signs in at the official service the way a person does: "Sign in" on the welcome page (from the list of a phone
    /// used without an account, by way of its "Sign in"), the code, the browser's part played by the test.
    /// </summary>
    public void SignIn(FakeRelay relay, string account = "ada")
    {
        if (Shell.Top is HostsPage) Model<HostsViewModel>().SignInCommand.Execute(null);
        var welcome = Model<WelcomeViewModel>();
        var flow = welcome.SignIn;
        welcome.SignInCommand.Execute(null);
        Assert.True(Phone.Until(() => flow.HasCode || flow.HasError), "the page shows a code");
        Assert.True(flow.ErrorText is null, "signing in could not start: " + flow.ErrorText);
        Assert.True(relay.Approve(flow.UserCode!, account));
        Assert.True(Phone.Until(() => Shell.Top is HostsPage && Remote.Account.IsSignedIn), "the host list replaces the welcome page");
    }

    /// <summary>Pairs with a computer of the account the way a person does: its row, the number, "Allow" on the computer.</summary>
    public void Pair(Computer computer)
    {
        var hosts = Model<HostsViewModel>();
        Assert.True(Phone.Until(() => hosts.Unpaired.Any(row => row.Host.Id == computer.Identity.Id && row.IsPairAction)), "the computer is listed as one to pair with");
        computer.Service.OpenPairing(TimeSpan.FromMinutes(1));
        hosts.Unpaired.Single(row => row.Host.Id == computer.Identity.Id).OpenCommand.Execute(null);
        Assert.True(Phone.Until(() => Shell.Top is PairingPage && computer.Service.PairingRequests.Count == 1), "the computer is asked");
        computer.Service.PairingRequests[0].Allow();
        Assert.True(Phone.Until(() => Shell.Top is HostsPage && hosts.Online.Any(row => row.Host.Id == computer.Identity.Id)), "the computer is paired and connected");
        computer.Service.ClosePairing();
    }

    /// <summary>
    /// Adds a computer by what the person types or pastes on the "Add a computer" page, which has to be on top. With an
    /// address, the number is compared and allowed on the computer; with a pairing link there is none.
    /// </summary>
    public void Add(Computer computer, string target)
    {
        var add = Model<AddHostViewModel>();
        add.Target = target;
        add.ConnectCommand.Execute(null);
        if (!target.StartsWith("octopus://", StringComparison.Ordinal))
        {
            Assert.True(Phone.Until(() => Shell.Top is PairingPage && computer.Service.PairingRequests.Count == 1), "the computer is asked: " + add.ErrorText);
            computer.Service.PairingRequests[0].Allow();
        }
        Assert.True(Phone.Until(() => Shell.Top is HostsPage), "the list is back once the computer is paired: " + add.ErrorText);
        var hosts = Model<HostsViewModel>();
        Assert.True(Phone.Until(() => hosts.Online.Any(row => row.Host.Id == computer.Identity.Id)), "the computer is paired and connected");
    }

    public void Dispose()
    {
        Window.Close();
        Remote.Dispose();
    }
}
