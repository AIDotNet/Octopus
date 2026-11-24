using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
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
using Octopus.Storage;
using Octopus.Terminal;
using Octopus.Terminal.Avalonia;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>
/// The relay inside the app (design/12-远程操作.md §8, R4): the settings that put this computer on a relay, and
/// reaching a host that is on no address this computer can dial.
/// </summary>
public class RemoteRelayTests
{
    /// <summary>The other computer: its own projects and host service, reachable only through the relay.</summary>
    private sealed class OtherComputer : IAsyncDisposable
    {
        public DeviceIdentity Identity { get; }
        public ProjectService Projects { get; }
        public HostWorkspace Workspace { get; }
        public RemoteHostService Service { get; }
        public RelayHostLink Link { get; }
        public TerminalSessionRegistry Sessions { get; } = new();

        private OtherComputer(RelayEndpoint relay, DeviceIdentity identity)
        {
            Identity = identity;
            Projects = new ProjectService(new InMemoryProjectStore(), AppHost.Get<PathComparer>());
            Workspace = new HostWorkspace(Projects, AppHost.Get<IItemLaunchService>(), AppHost.Get<ITerminalConnectionFactory>(), Sessions, AppHost.Get<ITabContentRegistry>(),
                AppHost.Get<IItemActivityService>(), AppHost.Get<IAgentCatalog>(), AppHost.Get<IAgentScanner>(), AppHost.Get<ISettingsStore>(), AppHost.Get<IShellResolver>(),
                AppHost.Get<Octopus.FileSystem.IFileTreeService>(), AppHost.Get<Octopus.Git.IGitService>(), AppHost.Get<PathComparer>());
            Service = new RemoteHostService(Identity, () => new DeviceDescription("Studio", DeviceKind.Desktop), new PairedPeerStore(null), Workspace, RemoteHostRuntime.Hello);
            Projects.AddProject(Path.GetTempPath(), "a-project-only-the-two-may-know");
            Link = new RelayHostLink(relay, Identity, () => "Studio", transport => Service.ServeAsync(transport));
            Link.Start();
        }

        /// <summary>The other computer, signed in to an account at the relay (the test confirms its code) and registered there.</summary>
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

    private static async Task<T> Wait<T>(Task<T> task, int milliseconds = 20_000)
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

    private static async Task Until(Func<bool> done, string what, int milliseconds = 20_000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!done() && Environment.TickCount64 < end)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(done(), what);
    }

    /// <summary>Stands in for the system's browser: what the page asked to be opened.</summary>
    private sealed class RecordingOpener : IFileOpener
    {
        public List<string> Urls { get; } = [];
        public void Open(string path) { }
        public void Reveal(string path) { }
        public bool OpenUrl(string url)
        {
            Urls.Add(url);
            return true;
        }
    }

    private static RemoteAccessSettingsViewModel NewPage(RecordingOpener? opener = null)
    {
        AppHost.Get<RelayAccess>().PollInterval = TimeSpan.FromMilliseconds(30);
        return new RemoteAccessSettingsViewModel(AppHost.Get<ISettingsStore>(), AppHost.Get<RemoteHostRuntime>(), AppHost.Get<RelayAccess>(), opener ?? new RecordingOpener());
    }

    /// <summary>This computer signs in at the relay (as the settings page does), the test confirming the code in the browser's place.</summary>
    private static async Task SignIn(FakeRelay relay, string account = "ada")
    {
        var access = AppHost.Get<RelayAccess>();
        access.PollInterval = TimeSpan.FromMilliseconds(30);
        await Wait(access.SetUrlAsync(relay.Url));
        var login = await Wait(access.BeginSignInAsync("This Mac"));
        Assert.True(relay.Approve(login.UserCode, account));
        Assert.Equal(RelaySignInResult.SignedIn, await Wait(access.CompleteSignInAsync(login)));
    }

    /// <summary>Signs in on the page: presses the button, waits for the code, confirms it.</summary>
    private static async Task SignInOnPage(RemoteAccessSettingsViewModel page, FakeRelay relay)
    {
        page.SignInRelayCommand.Execute(null);
        await Until(() => page.RelayLoginCode is not null || page.HasRelayError, "the page shows a code");
        Assert.Null(page.RelayErrorText);
        Assert.True(relay.Approve(page.RelayLoginCode!));
        await Wait(page.RelaySignIn);
    }

    [AvaloniaFact]
    public async Task Signing_in_on_the_settings_page_puts_this_computer_on_the_relay()
    {
        await using var relay = new FakeRelay();
        var runtime = AppHost.Get<RemoteHostRuntime>();
        var opener = new RecordingOpener();
        using var page = NewPage(opener);

        // Default official service is pre-selected; turning relay on without signing in asks to sign in first.
        Assert.Equal("https://octoterm.si", page.RelayUrl);
        page.RelayHostEnabled = true;
        await Wait(page.Applied);
        Assert.Null(runtime.RelayState);
        Assert.Equal("先登录中转服务", page.RelayStatus);
        page.RelayUrl = relay.Url;
        Assert.False(page.IsRelayInsecure); // this computer itself
        page.SaveRelayCommand.Execute(null);
        await Wait(page.Applied);
        Assert.Equal("先登录中转服务", page.RelayStatus);
        Assert.Equal("未登录", page.RelayAccountText);
        Assert.True(page.CanSignInRelay);
        Assert.Empty(relay.Hosts);

        // Sign in: the code is shown here and the relay's page is opened with it; nothing is typed into the app.
        page.SignInRelayCommand.Execute(null);
        await Until(() => page.RelayLoginCode is not null, "the page shows a code");
        Assert.True(page.IsRelaySigningIn);
        Assert.False(page.CanSignInRelay);
        Assert.Matches("^[B-Z]{4}-[B-Z]{4}$", page.RelayLoginCode!);
        Assert.Equal([page.RelayLoginCode!], relay.PendingCodes);
        Assert.Equal([$"{relay.Url}/device?code={page.RelayLoginCode}"], opener.Urls);
        Assert.Equal([runtime.DeviceName], relay.LoginNames);
        page.OpenRelayLoginCommand.Execute(null);
        Assert.Equal(2, opener.Urls.Count);

        // Confirmed in the browser: this computer is signed in and registers.
        Assert.True(relay.Approve(page.RelayLoginCode!));
        await Wait(page.RelaySignIn);
        Assert.True(page.IsRelaySignedIn);
        Assert.False(page.IsRelaySigningIn);
        Assert.Null(page.RelayLoginCode);
        Assert.Equal("Ada · ada@example.com", page.RelayAccountText);
        await Until(() => page.IsRelayOnline, "this computer registers at the relay: " + page.RelayStatus);
        Assert.Equal("已连接到中转", page.RelayStatus);
        Assert.Equal([runtime.Service!.Identity.Id], relay.Hosts);
        Assert.False(page.IsListening); // the local network stays closed: the two switches are separate
        // The address is a setting; the sign-in is in the keystore and nowhere in the settings.
        Assert.Equal(relay.Url, AppHost.Get<ISettingsStore>().Current.Remote.RelayUrl);
        var kept = await AppHost.Get<ISecretStore>().GetAsync("Octopus/remote", "relay-session");
        Assert.Contains("\"refreshToken\":\"or1.", kept);
        Assert.Contains("\"accountName\":\"Ada\"", kept);

        // A pairing link now carries the way through the relay.
        page.StartPairingCommand.Execute(null);
        await Until(() => page.HasPairingLink, "a pairing link is shown");
        var link = PairingLink.TryParse(page.PairingLink)!;
        Assert.Contains(RemoteClientRuntime.RelayAddress(runtime.Service.Identity.Id).ToString(), link.Addresses);
        page.StopPairingCommand.Execute(null);

        // Plain http to another machine would send the sign-in in the clear: the page warns before anything is saved.
        page.RelayUrl = "http://relay.example.com";
        Assert.True(page.IsRelayInsecure);
        page.RelayUrl = "relay.example.com";
        Assert.False(page.IsRelayInsecure);
        page.RelayUrl = relay.Url;

        // Signed out: gone from the relay, and nothing of the sign-in is kept.
        page.SignOutRelayCommand.Execute(null);
        await Wait(page.Applied);
        await Until(() => relay.Hosts.Count == 0, "this computer left the relay");
        Assert.False(page.IsRelaySignedIn);
        Assert.False(page.IsRelayOnline);
        Assert.False(page.RelaySignInEnded); // it was this computer's own doing
        Assert.Equal("未登录", page.RelayAccountText);
        Assert.Equal("先登录中转服务", page.RelayStatus);
        Assert.Null(await AppHost.Get<ISecretStore>().GetAsync("Octopus/remote", "relay-session"));
        Assert.Null(await Wait(AppHost.Get<RelayAccess>().GetAsync()));
    }

    [AvaloniaFact]
    public async Task A_sign_in_that_is_refused_given_up_or_cannot_start_leaves_this_computer_signed_out()
    {
        await using var relay = new FakeRelay();
        using var page = NewPage();
        page.RelayUrl = relay.Url;

        // Refused in the browser.
        page.SignInRelayCommand.Execute(null);
        await Until(() => page.RelayLoginCode is not null, "the page shows a code");
        Assert.True(relay.Deny(page.RelayLoginCode!));
        await Wait(page.RelaySignIn);
        Assert.Equal("登录在浏览器里被拒绝了。", page.RelayErrorText);
        Assert.False(page.IsRelaySignedIn);
        Assert.False(page.IsRelaySigningIn);

        // Given up on this side: no complaint, and the button is back.
        page.SignInRelayCommand.Execute(null);
        await Until(() => page.RelayLoginCode is not null, "the page shows a code again");
        Assert.Null(page.RelayErrorText);
        page.CancelRelaySignInCommand.Execute(null);
        await Wait(page.RelaySignIn);
        Assert.Null(page.RelayErrorText);
        Assert.True(page.CanSignInRelay);

        // No relay at that address, and an invalid address.
        page.RelayUrl = "http://127.0.0.1:9";
        page.SignInRelayCommand.Execute(null);
        await Wait(page.RelaySignIn);
        Assert.StartsWith("登录失败：", page.RelayErrorText);
        page.RelayUrl = "two words";
        page.SignInRelayCommand.Execute(null);
        await Wait(page.RelaySignIn);
        Assert.Equal("先填写中转地址", page.RelayErrorText);
        Assert.Null(await AppHost.Get<ISecretStore>().GetAsync("Octopus/remote", "relay-session"));

        // Clearing the address falls back to the official default service.
        page.RelayUrl = "";
        page.SaveRelayCommand.Execute(null);
        await Wait(page.Applied);
        Assert.Equal("https://octoterm.si", page.RelayUrl);
    }

    [AvaloniaFact]
    public async Task A_token_that_ran_out_is_replaced_and_the_sign_in_survives_a_restart()
    {
        await using var relay = new FakeRelay();
        using var page = NewPage();
        page.RelayUrl = relay.Url;
        await SignInOnPage(page, relay);
        page.RelayHostEnabled = true;
        await Wait(page.Applied);
        await Until(() => page.IsRelayOnline, "this computer registers at the relay: " + page.RelayStatus);
        var first = await AppHost.Get<ISecretStore>().GetAsync("Octopus/remote", "relay-session");

        // The access token's time is up and the connection drops: the next one is made with a new token, by itself.
        relay.ExpireAccessTokens();
        relay.DropHosts();
        await Until(() => relay.Refreshes == 1 && relay.Hosts.Count == 1 && page.IsRelayOnline, "this computer is back on the relay with a new token: " + page.RelayStatus);
        Assert.True(page.IsRelaySignedIn);
        // The refresh token was replaced with it, and the new one is what is kept.
        var second = await AppHost.Get<ISecretStore>().GetAsync("Octopus/remote", "relay-session");
        Assert.NotEqual(first, second);

        // The app starts again: what was kept signs it in without anyone doing anything.
        var restarted = new RelayAccess(AppHost.Get<ISettingsStore>(), AppHost.Get<ISecretStore>(), AppHost.Get<IAppPaths>(), AppHost.Get<RemoteIdentityProvider>());
        await Wait(restarted.LoadAsync());
        Assert.Equal("Ada", restarted.Account!.Name);
        var endpoint = await Wait(restarted.GetAsync());
        Assert.StartsWith("oa1.", endpoint!.Token);
        Assert.Equal(2, relay.Refreshes);
    }

    [AvaloniaFact]
    public async Task A_computer_removed_from_its_account_is_signed_out_here_and_says_so()
    {
        await using var relay = new FakeRelay();
        var runtime = AppHost.Get<RemoteHostRuntime>();
        using var page = NewPage();
        page.RelayUrl = relay.Url;
        await SignInOnPage(page, relay);
        page.RelayHostEnabled = true;
        await Wait(page.Applied);
        await Until(() => page.IsRelayOnline, "this computer registers at the relay: " + page.RelayStatus);

        // Removed on the account page (from another device): the relay closes the connection and no token works again.
        relay.SignOut(runtime.Service!.Identity.Id);

        await Until(() => !page.IsRelaySignedIn, "the page shows this computer as signed out: " + page.RelayStatus);
        Assert.True(page.RelaySignInEnded);
        Assert.False(page.IsRelayOnline);
        Assert.Equal("未登录", page.RelayAccountText);
        await Until(() => page.RelayStatus == "先登录中转服务", "the page asks to sign in: " + page.RelayStatus);
        Assert.Null(await AppHost.Get<ISecretStore>().GetAsync("Octopus/remote", "relay-session"));
        Assert.Empty(relay.Hosts);

        // Signing in again sets it right.
        await SignInOnPage(page, relay);
        Assert.False(page.RelaySignInEnded);
        await Until(() => page.IsRelayOnline, "this computer is on the relay again: " + page.RelayStatus);
    }

    [AvaloniaFact]
    public async Task A_host_on_no_reachable_address_is_reached_through_the_relay()
    {
        await using var relay = new FakeRelay();
        await using var other = await OtherComputer.StartAsync(relay);
        await SignIn(relay);
        var runtime = AppHost.Get<RemoteClientRuntime>();
        var mine = await Wait(AppHost.Get<RemoteIdentityProvider>().GetAsync());
        other.Service.Devices.Add(mine.PublicKey, runtime.Description);
        // The address the host had at home answers nothing from here.
        runtime.Hosts.Add(other.Identity.PublicKey, new DeviceDescription("Studio", DeviceKind.Desktop), ["ws://127.0.0.1:9"]);
        await Until(() => other.Link.State == RelayLinkState.Online, "the host is on the relay");

        var client = await Wait(runtime.ConnectAsync(other.Identity.Id));

        Assert.True(runtime.IsThroughRelay(other.Identity.Id));
        Assert.Equal("a-project-only-the-two-may-know", (await Wait(client.ListProjectsAsync())).Single().Name);
        // The address that did not answer is still the host's only one: the relay is a way, not an address of its own.
        Assert.Equal(["ws://127.0.0.1:9"], runtime.Hosts.Find(other.Identity.Id)!.Addresses);
        using var page = ActivatorUtilities.CreateInstance<RemoteHostsSettingsViewModel>(AppHost.Services);
        Assert.Equal("经中转", page.Hosts.Single().Address);
        // What the relay carried says nothing of what the two said to each other.
        var carried = Encoding.Latin1.GetString(relay.Carried.SelectMany(message => message).ToArray());
        Assert.DoesNotContain("a-project-only-the-two-may-know", carried);
        Assert.DoesNotContain("Studio", carried);
    }

    [AvaloniaFact]
    public async Task A_paired_host_of_another_account_is_not_reached_through_the_relay()
    {
        await using var relay = new FakeRelay();
        await using var other = await OtherComputer.StartAsync(relay, account: "bob");
        await SignIn(relay);
        var runtime = AppHost.Get<RemoteClientRuntime>();
        var mine = await Wait(AppHost.Get<RemoteIdentityProvider>().GetAsync());
        // Paired, even: pairing says who may operate, the account says whom the relay joins. Both are needed.
        other.Service.Devices.Add(mine.PublicKey, runtime.Description);
        runtime.Hosts.Add(other.Identity.PublicKey, new DeviceDescription("Studio", DeviceKind.Desktop), []);
        await Until(() => other.Link.State == RelayLinkState.Online, "the host is on the relay");

        await Assert.ThrowsAnyAsync<IOException>(() => Wait(runtime.ConnectAsync(other.Identity.Id)));

        Assert.NotEqual(RemoteHostState.Connected, runtime.StateOf(other.Identity.Id));
        Assert.Empty(relay.Carried);
    }

    [AvaloniaFact]
    public async Task Pairing_works_through_the_relay_with_a_link_and_a_stranger_is_still_turned_away()
    {
        await using var relay = new FakeRelay();
        await using var other = await OtherComputer.StartAsync(relay);
        await SignIn(relay);
        var runtime = AppHost.Get<RemoteClientRuntime>();
        await Until(() => other.Link.State == RelayLinkState.Online, "the host is on the relay");

        // Not paired, and the host is not pairing: being signed in to the same account alone opens nothing.
        var address = RemoteClientRuntime.RelayAddress(other.Identity.Id).ToString();
        var refused = await Assert.ThrowsAsync<HandshakeException>(() => Wait(runtime.PairAsync(address, null)));
        Assert.Equal(HandshakeStatus.PairingClosed, refused.Status);

        other.Service.OpenPairing(TimeSpan.FromMinutes(1));
        var link = other.Service.CreatePairingLink([address])!;
        var peer = await Wait(runtime.PairAsync(link.ToString(), null));

        Assert.Equal(other.Identity.Id, peer.Id);
        Assert.Empty(peer.Addresses);
        Assert.True(runtime.IsThroughRelay(peer.Id));
        Assert.Equal(RemoteHostState.Connected, runtime.StateOf(peer.Id));
        Assert.Single(other.Service.Devices.Peers);
    }

    [AvaloniaFact]
    public async Task Render_the_page_signing_in_and_with_this_computer_on_the_relay()
    {
        await using var relay = new FakeRelay();
        var directory = Path.Combine(AppContext.BaseDirectory, "shots");
        Directory.CreateDirectory(directory);
        using var page = NewPage();
        page.RelayUrl = relay.Url;
        page.RelayHostEnabled = true;
        await Wait(page.Applied);

        // While the code waits to be confirmed in the browser.
        page.SignInRelayCommand.Execute(null);
        await Until(() => page.RelayLoginCode is not null, "the page shows a code");
        var code = page.RelayLoginCode!;
        Shoot("remote-relay-signing-in");

        Assert.True(relay.Approve(code));
        await Wait(page.RelaySignIn);
        await Until(() => page.IsRelayOnline, "this computer registers at the relay");
        // What the page shows for a relay elsewhere that is plain http.
        page.RelayUrl = "http://relay.example.com";
        Shoot("remote-relay");

        Assert.True(File.Exists(Path.Combine(directory, "remote-relay-light.png")));
        Assert.True(File.Exists(Path.Combine(directory, "remote-relay-signing-in-dark.png")));

        void Shoot(string name)
        {
            foreach (var dark in new[] { false, true })
            {
                TKCode.Arc.ArcTheme.Apply(t => t.Appearance(dark ? TKCode.Arc.ArcAppearance.Dark : TKCode.Arc.ArcAppearance.Light));
                var window = new Avalonia.Controls.Window
                {
                    Width = 760, Height = 1000,
                    Content = new Avalonia.Controls.ScrollViewer { Padding = new Avalonia.Thickness(28), Content = new RemoteAccessPageView { DataContext = page } },
                };
                window.Styles.Add(new Octopus.App.Common.AppStyles());
                window.Show();
                Frames.Save(window, Path.Combine(directory, $"{name}-{(dark ? "dark" : "light")}.png"));
                window.Close();
            }
        }
    }

    [Fact]
    public void A_relay_address_is_an_address_of_a_host_and_nothing_else_is()
    {
        var address = RemoteClientRuntime.RelayAddress("b3j2nk4vp73plguwgcshhuy2pu");
        Assert.Equal(RemoteClientRuntime.RelayScheme, RemoteClientRuntime.ParseAddress(address.ToString())!.Scheme);
        Assert.Equal("b3j2nk4vp73plguwgcshhuy2pu", RemoteClientRuntime.ParseAddress(address.ToString())!.Host);
        Assert.Null(RemoteClientRuntime.ParseAddress("octopus-relay://short"));
        Assert.Null(RemoteClientRuntime.ParseAddress("octopus-relay://b3j2nk4vp73plguwgcshhuy2pu/../x y"));
    }
}
