using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Octopus.Agents;
using Octopus.App.Activity;
using Octopus.App.Common;
using Octopus.App.Composition;
using Octopus.App.Remote;
using Octopus.App.Services;
using Octopus.App.Settings.Remote;
using Octopus.App.Workspace;
using Octopus.Platform;
using Octopus.Remote.Client;
using Octopus.Remote.Host;
using Octopus.Remote.Relay;
using Octopus.Remote.Security;
using Octopus.Remote.Tests;
using Octopus.Remote.Transport;
using Octopus.Storage;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>
/// The account's devices and this computer's access password (design/16-账号设备与访问密码.md): the settings page, the
/// password in the keystore, what the relay is told, and getting into another computer of the account with its password.
/// </summary>
public class AccountDevicesTests
{
    private const string StudioPassword = "K7PQ2M9X";

    /// <summary>Another computer of the account, reachable only through the relay, that takes its access password.</summary>
    private sealed class OtherComputer : IAsyncDisposable
    {
        public DeviceIdentity Identity { get; }
        public ProjectService Projects { get; }
        public HostWorkspace Workspace { get; }
        public RemoteHostService Service { get; }
        public RelayHostLink Link { get; }

        private OtherComputer(RelayEndpoint relay, DeviceIdentity identity)
        {
            Identity = identity;
            Projects = new ProjectService(new InMemoryProjectStore(), AppHost.Get<PathComparer>());
            Workspace = new HostWorkspace(Projects, AppHost.Get<IItemLaunchService>(), AppHost.Get<ITerminalConnectionFactory>(), new TerminalSessionRegistry(), AppHost.Get<ITabContentRegistry>(),
                AppHost.Get<IItemActivityService>(), AppHost.Get<IAgentCatalog>(), AppHost.Get<IAgentScanner>(), AppHost.Get<ISettingsStore>(), AppHost.Get<IShellResolver>(),
                AppHost.Get<Octopus.FileSystem.IFileTreeService>(), AppHost.Get<Octopus.Git.IGitService>(), AppHost.Get<PathComparer>());
            Service = new RemoteHostService(Identity, () => new DeviceDescription("Studio", DeviceKind.Desktop), new PairedPeerStore(null), Workspace, RemoteHostRuntime.Hello);
            Service.SetAccessPassword(StudioPassword);
            Projects.AddProject(Path.GetTempPath(), "studio-project");
            Link = new RelayHostLink(relay, Identity, () => "Studio", transport => Service.ServeAsync(transport))
            {
                Features = () => Service.AcceptsPassword ? [RelayHostFeatures.PasswordAccess] : [],
            };
            Link.Start();
        }

        public static async Task<OtherComputer> StartAsync(FakeRelay relay, string account = "ada")
        {
            var identity = DeviceIdentity.Create();
            var url = new Uri(relay.Url);
            var login = await Wait(RelayAccountClient.StartLoginAsync(url, identity, "Studio"));
            Assert.True(relay.Approve(login.UserCode, account));
            var (status, tokens) = await Wait(RelayAccountClient.PollLoginAsync(url, login, identity));
            Assert.Equal(RelayLoginStatus.SignedIn, status);
            return new OtherComputer(new RelayEndpoint(url, tokens!.AccessToken), identity);
        }

        public async ValueTask DisposeAsync()
        {
            await Link.DisposeAsync();
            await Service.DisposeAsync();
            Workspace.Dispose();
            Identity.Dispose();
        }
    }

    private static async Task<T> Wait<T>(Task<T> task, int milliseconds = 30_000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!task.IsCompleted && Environment.TickCount64 < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(task.IsCompleted, "The operation did not finish in time.");
        return await task;
    }

    private static Task Wait(Task task) => Wait(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }));

    private static async Task Until(Func<bool> done, string what, int milliseconds = 30_000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!done() && Environment.TickCount64 < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(done(), what);
    }

    private static AccountDevicesSettingsViewModel NewPage() =>
        new(AppHost.Get<ISettingsStore>(), AppHost.Get<RemoteHostRuntime>(), AppHost.Get<RemoteClientRuntime>(), AppHost.Get<RelayAccess>());

    private static async Task SignIn(FakeRelay relay, string account = "ada")
    {
        var access = AppHost.Get<RelayAccess>();
        access.PollInterval = TimeSpan.FromMilliseconds(30);
        await Wait(access.SetUrlAsync(relay.Url));
        var login = await Wait(access.BeginSignInAsync("This Mac"));
        Assert.True(relay.Approve(login.UserCode, account));
        Assert.Equal(RelaySignInResult.SignedIn, await Wait(access.CompleteSignInAsync(login)));
    }

    [AvaloniaFact]
    public async Task The_password_is_made_once_kept_in_the_keystore_hidden_until_shown_and_follows_the_switch()
    {
        var runtime = AppHost.Get<RemoteHostRuntime>();
        using var page = NewPage();
        await Wait(page.Loaded);

        // Hidden by default; shown on request, as eight characters people do not mix up.
        Assert.Equal("••••-••••", page.PasswordText);
        Assert.False(page.IsSignedIn);
        page.ToggleRevealCommand.Execute(null);
        Assert.Matches("^[A-HJKMNP-Z2-9]{4}-[A-HJKMNP-Z2-9]{4}$", page.PasswordText);
        var made = page.PasswordText.Replace("-", "");
        Assert.Equal(made, await AppHost.Get<ISecretStore>().GetAsync("Octopus/remote", RemoteHostRuntime.AccessPasswordName));
        // In the keystore only: not in the settings.
        Assert.DoesNotContain(made, System.Text.Json.JsonSerializer.Serialize(AppHost.Get<ISettingsStore>().Current.Remote));
        Assert.Equal(made, runtime.Service!.AccessPassword);
        Assert.True(runtime.AcceptsPassword);
        page.ToggleRevealCommand.Execute(null);
        Assert.Equal("••••-••••", page.PasswordText);

        // Off: the service takes no password; on again: the same one.
        page.PasswordAccessEnabled = false;
        await Wait(page.Applied);
        Assert.Null(runtime.Service.AccessPassword);
        Assert.False(AppHost.Get<ISettingsStore>().Current.Remote.PasswordAccessEnabled);
        page.PasswordAccessEnabled = true;
        await Wait(page.Applied);
        Assert.Equal(made, runtime.Service.AccessPassword);

        // A password of one's own: exactly eight letters and digits, checked before anything is kept.
        foreach (var bad in new[] { "abc", "ABCDEFGHJ", "ABCD_EFG", "密码密码密码密码" })
        {
            page.BeginEditPasswordCommand.Execute(null);
            page.NewPassword = bad;
            page.SavePasswordCommand.Execute(null);
            await Wait(page.Applied);
            Assert.True(page.HasPasswordError, bad);
            Assert.Equal(made, runtime.Service.AccessPassword);
        }
        page.NewPassword = "k7pq-2m9x";
        Assert.False(page.HasPasswordError);
        page.SavePasswordCommand.Execute(null);
        await Wait(page.Applied);
        Assert.False(page.IsEditingPassword);
        Assert.Equal("K7PQ-2M9X", page.PasswordText); // a new password is shown once
        Assert.Equal("K7PQ2M9X", runtime.Service.AccessPassword);
        Assert.Equal("K7PQ2M9X", await AppHost.Get<ISecretStore>().GetAsync("Octopus/remote", RemoteHostRuntime.AccessPasswordName));

        // A new random one.
        page.RegeneratePasswordCommand.Execute(null);
        await Wait(page.Applied);
        Assert.NotEqual("K7PQ2M9X", runtime.Service.AccessPassword);
        Assert.Equal(AccessPassword.Format(runtime.Service.AccessPassword!), page.PasswordText);
    }

    [AvaloniaFact]
    public async Task Another_computer_of_the_account_is_reached_with_its_password_once_and_remembered()
    {
        await using var relay = new FakeRelay();
        await using var other = await OtherComputer.StartAsync(relay);
        relay.AddDevice("aaaaaaaaaaaaaaaaaaaaaaaaaa", "Office PC");
        relay.AddDevice("bbbbbbbbbbbbbbbbbbbbbbbbbb", "Ada's iPhone", "phone");
        relay.AddDevice("cccccccccccccccccccccccccc", "Bob's Mac", account: "bob");
        await Until(() => other.Link.State == RelayLinkState.Online, "the other computer is on the relay");
        await Until(() => relay.FeaturesOf(other.Identity.Id).Contains(RelayHostFeatures.PasswordAccess), "the other computer says it takes passwords");
        await SignIn(relay);
        var client = AppHost.Get<RemoteClientRuntime>();

        using var page = NewPage();
        await Wait(page.Loaded);
        Assert.True(page.IsSignedIn);
        await Until(() => page.Devices.Count == 4, "the account's four devices are listed (not Bob's)");
        var self = Assert.Single(page.Devices, row => row.IsSelf);
        Assert.Equal("This Mac", self.Name);
        var office = page.Devices.Single(row => row.Name == "Office PC");
        Assert.True(office.IsOffline);
        Assert.False(office.NeedsPassword);
        Assert.Equal(AccountDeviceState.Phone, page.Devices.Single(row => row.Name == "Ada's iPhone").State);
        var studio = page.Devices.Single(row => row.Name == "Studio");
        Assert.True(studio.IsOnline);
        Assert.True(studio.NeedsPassword);
        Assert.DoesNotContain(page.Devices, row => row.Name == "Bob's Mac");

        // A wrong password: turned away, nothing remembered on either side.
        studio.BeginEnteringCommand.Execute(null);
        studio.Password = "AAAA-BBBB";
        Assert.True(studio.CanSubmit);
        studio.SubmitCommand.Execute(null);
        await Wait(studio.Connecting);
        Assert.Equal("密码不正确。", studio.ErrorText);
        Assert.True(studio.IsEntering);
        Assert.Null(client.Hosts.Find(other.Identity.Id));
        Assert.Empty(other.Service.Devices.Peers);

        // Not eight characters yet: the button waits.
        studio.Password = "k7pq-2m9";
        Assert.False(studio.CanSubmit);

        // The right one, typed as people do: in, remembered as paired by password on both sides, connected.
        studio.Password = "k7pq 2m9x";
        studio.SubmitCommand.Execute(null);
        await Wait(studio.Connecting);
        Assert.Null(studio.ErrorText);
        Assert.False(studio.IsEntering);
        var remembered = client.Hosts.Find(other.Identity.Id);
        Assert.NotNull(remembered);
        Assert.Equal(PairedVia.Password, remembered!.Via);
        Assert.Equal(PairedVia.Password, Assert.Single(other.Service.Devices.Peers).Via);
        Assert.Equal(RemoteHostState.Connected, client.StateOf(other.Identity.Id));
        Assert.True(client.IsThroughRelay(other.Identity.Id));
        Assert.Equal("studio-project", (await Wait(client.ClientOf(other.Identity.Id)!.ListProjectsAsync())).Single().Name);
        await Until(() => page.Devices.SingleOrDefault(row => row.Name == "Studio")?.IsAuthorized == true, "the row shows the computer as authorized");

        // Next time no password: the ordinary connection to a paired host.
        await Wait(client.DisconnectAsync(other.Identity.Id));
        Assert.NotNull(await Wait(client.ConnectAsync(other.Identity.Id)));

        // The relay carried no password, nor anything else in the clear.
        var carried = System.Text.Encoding.Latin1.GetString(relay.Carried.SelectMany(message => message).ToArray());
        Assert.DoesNotContain(StudioPassword, carried);
        Assert.DoesNotContain("K7PQ", carried, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("studio-project", carried);
    }

    [AvaloniaFact]
    public async Task The_relay_hears_whether_this_computer_takes_passwords_and_too_many_wrong_ones_pause_it()
    {
        await using var relay = new FakeRelay();
        await SignIn(relay);
        var runtime = AppHost.Get<RemoteHostRuntime>();
        AppHost.Get<ISettingsStore>().Update(s => s.Remote.RelayHostEnabled = true);
        using var page = NewPage();
        await Wait(page.Loaded);
        await Wait(runtime.ApplyAsync());
        var me = runtime.Service!.Identity.Id;
        await Until(() => relay.FeaturesOf(me).Contains(RelayHostFeatures.PasswordAccess), "registered with password access");

        page.PasswordAccessEnabled = false;
        await Wait(page.Applied);
        await Until(() => relay.FeaturesOf(me).Count == 0, "the relay hears password access is off");
        page.PasswordAccessEnabled = true;
        await Wait(page.Applied);
        await Until(() => relay.FeaturesOf(me).Contains(RelayHostFeatures.PasswordAccess), "and on again");

        // Ten wrong passwords within the hour, from two devices (each may try five): password access pauses.
        using var first = DeviceIdentity.Create();
        using var second = DeviceIdentity.Create();
        for (var i = 0; i < PasswordDoor.MaxFailuresInWindow; i++)
        {
            var e = await Assert.ThrowsAsync<HandshakeException>(() => Knock(runtime.Service, i < PasswordDoor.MaxFailuresPerDevice ? first : second, "WRONG234"));
            // The fifth wrong one from a device makes it wait.
            Assert.Equal(i % PasswordDoor.MaxFailuresPerDevice == PasswordDoor.MaxFailuresPerDevice - 1 ? HandshakeStatus.PasswordLocked : HandshakeStatus.WrongPassword, e.Status);
            if (i == PasswordDoor.MaxFailuresPerDevice - 1)
            {
                // Waiting: not even tried, and not counted again.
                Assert.Equal(HandshakeStatus.PasswordLocked, (await Assert.ThrowsAsync<HandshakeException>(() => Knock(runtime.Service, first, "WRONG234"))).Status);
                Assert.False(page.IsSuspended);
            }
        }
        await Until(() => page.IsSuspended, "the page says password access is paused");
        Assert.False(runtime.AcceptsPassword);
        await Until(() => relay.FeaturesOf(me).Count == 0, "the relay hears it is paused");
        // Even the right password is refused while paused.
        using var third = DeviceIdentity.Create();
        var right = await Wait(runtime.GetAccessPasswordAsync());
        var paused = await Assert.ThrowsAsync<HandshakeException>(() => Knock(runtime.Service, third, right));
        Assert.Equal(HandshakeStatus.PairingClosed, paused.Status);

        page.ResumePasswordAccessCommand.Execute(null);
        Assert.False(page.IsSuspended);
        await Until(() => relay.FeaturesOf(me).Contains(RelayHostFeatures.PasswordAccess), "the relay hears it is back");
        var channel = await Knock(runtime.Service, third, right);
        Assert.Equal(PairedVia.Password, runtime.Service.Devices.Find(third.Id)!.Via);
        await channel.DisposeAsync();
    }

    /// <summary>A device at this computer's door with a password, over a pipe in memory.</summary>
    private static async Task<SecureChannel> Knock(RemoteHostService host, DeviceIdentity device, string password)
    {
        var (a, b) = InMemoryTransport.CreatePair();
        _ = host.ServeAsync(b);
        return await Wait(SecureChannel.ConnectAsync(a, device, new DeviceDescription("Stranger", DeviceKind.Phone), PeerExpectation.WithDeviceId(host.Identity.Id), new ConnectOptions { Password = password }));
    }
}
