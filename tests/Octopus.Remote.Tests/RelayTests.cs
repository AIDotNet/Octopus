using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Octopus.Remote.Relay;
using Octopus.Remote.Security;
using Octopus.Remote.Transport;
using Xunit;

namespace Octopus.Remote.Tests;

/// <summary>This app's side of a relay (design/12-远程操作.md §8): the host's standing connection, the device's way in, and what the relay gets to see.</summary>
public class RelayTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);
    private static readonly DeviceDescription Station = new("Studio-Secret-Name", DeviceKind.Desktop);
    private static readonly DeviceDescription Laptop = new("Laptop-Secret-Name", DeviceKind.Desktop);

    private static async Task Until(Func<bool> done, string what)
    {
        var end = Environment.TickCount64 + (long)Limit.TotalMilliseconds;
        while (!done() && Environment.TickCount64 < end) await Task.Delay(10);
        Assert.True(done(), what);
    }

    /// <summary>A host that lets one paired device in and answers each message with "echo: " and the same words.</summary>
    private static RelayHostLink EchoHost(RelayEndpoint relay, DeviceIdentity host, DeviceIdentity device) =>
        new(relay, host, () => Station.Name, async transport =>
        {
            await using var channel = await SecureChannel.AcceptAsync(transport, host, Station, new Door { Paired = { device.PublicKey } });
            while (await channel.ReceiveAsync() is { } message)
            {
                await channel.SendAsync(Encoding.UTF8.GetBytes("echo: " + Encoding.UTF8.GetString(message)));
            }
        }) { RetryPause = TimeSpan.FromMilliseconds(50) };

    private static async Task<string> AskAsync(RelayEndpoint relay, DeviceIdentity host, DeviceIdentity device, string words)
    {
        var transport = await RelayDialer.ConnectAsync(relay, host.Id).WaitAsync(Limit);
        await using var channel = await SecureChannel.ConnectAsync(transport, device, Laptop, PeerExpectation.Paired(host.PublicKey)).WaitAsync(Limit);
        Assert.Equal(Station.Name, channel.PeerDescription.Name);
        await channel.SendAsync(Encoding.UTF8.GetBytes(words));
        return Encoding.UTF8.GetString((await channel.ReceiveAsync().AsTask().WaitAsync(Limit))!);
    }

    [Fact]
    public void A_relay_is_named_by_its_address_and_needs_a_token()
    {
        Assert.Equal("https://relay.example.com/", RelayEndpoint.TryCreate(" relay.example.com ", "t")!.Url.ToString());
        Assert.Equal("https://relay.example.com/octopus/", RelayEndpoint.TryCreate("wss://relay.example.com/octopus", "t")!.Url.ToString());
        var local = RelayEndpoint.TryCreate("http://127.0.0.1:8080/", " t ")!;
        Assert.Equal("ws://127.0.0.1:8080/v1/host", local.WebSocket("v1/host").ToString());
        Assert.Equal("http://127.0.0.1:8080/v1/tunnels", local.Http("v1/tunnels").ToString());
        Assert.Equal("t", local.Token);
        Assert.DoesNotContain("t", local.ToString().Replace("http", ""));
        Assert.Null(RelayEndpoint.TryCreate("relay.example.com", ""));
        Assert.Null(RelayEndpoint.TryCreate("", "t"));
        Assert.Null(RelayEndpoint.TryCreate("ftp://relay.example.com", "t"));
        Assert.Null(RelayEndpoint.TryCreate("two words", "t"));
    }

    [Fact]
    public void The_device_id_is_the_one_the_relay_derives_from_the_same_key()
    {
        // The vector of the relay's design (§3), also in the relay's own tests: 0x04 followed by 0x01…0x40.
        byte[] key = [0x04, .. Enumerable.Range(1, 64).Select(i => (byte)i)];
        Assert.Equal("b3j2nk4vp73plguwgcshhuy2pu", Base32.Encode(SHA256.HashData(key).AsSpan(0, 16)));
        using var identity = DeviceIdentity.Create();
        Assert.Equal(Base32.Encode(SHA256.HashData(identity.PublicKey.Bytes).AsSpan(0, 16)), identity.Id);
    }

    [Fact]
    public async Task A_device_reaches_a_host_through_a_relay_that_sees_only_ciphertext()
    {
        await using var relay = new FakeRelay();
        using var host = DeviceIdentity.Create();
        using var device = DeviceIdentity.Create();
        await using var link = EchoHost(relay.Endpoint, host, device);
        link.Start();
        await Until(() => link.State == RelayLinkState.Online, "the host registers at the relay");
        Assert.Equal([host.Id], relay.Hosts);

        Assert.Equal("echo: the words nobody else may read", await AskAsync(relay.Endpoint, host, device, "the words nobody else may read"));
        // A second device connection gets a tunnel of its own.
        Assert.Equal("echo: again", await AskAsync(relay.Endpoint, host, device, "again"));

        // Everything the relay carried, searched for anything it should not be able to read: the words, either answer,
        // and the two computers' names (which travel inside the handshake's encrypted part).
        var carried = relay.Carried.ToArray();
        Assert.True(carried.Length >= 12, "two handshakes and two exchanges went through the relay");
        var everything = Encoding.Latin1.GetString(carried.SelectMany(message => message).ToArray());
        foreach (var secret in new[] { "the words nobody else may read", "echo", "again", Station.Name, Laptop.Name })
        {
            Assert.DoesNotContain(secret, everything);
        }
    }

    [Fact]
    public async Task A_device_the_host_does_not_know_gets_nowhere_through_the_relay()
    {
        await using var relay = new FakeRelay();
        using var host = DeviceIdentity.Create();
        using var device = DeviceIdentity.Create();
        using var stranger = DeviceIdentity.Create();
        await using var link = EchoHost(relay.Endpoint, host, device);
        link.Start();
        await Until(() => link.State == RelayLinkState.Online, "the host registers at the relay");

        // Having the relay's token is not being paired: the host's own check refuses.
        var transport = await RelayDialer.ConnectAsync(relay.Endpoint, host.Id).WaitAsync(Limit);
        var refused = await Assert.ThrowsAsync<HandshakeException>(() => SecureChannel.ConnectAsync(transport, stranger, Laptop, PeerExpectation.Paired(host.PublicKey)).WaitAsync(Limit));
        Assert.Equal(HandshakeStatus.UnknownDevice, refused.Status);
    }

    [Fact]
    public async Task What_a_relay_refuses_is_said_as_what_it_is()
    {
        await using var relay = new FakeRelay();
        using var host = DeviceIdentity.Create();
        using var device = DeviceIdentity.Create();

        var offline = await Assert.ThrowsAsync<RelayException>(() => RelayDialer.ConnectAsync(relay.Endpoint, host.Id));
        Assert.Equal(RelayFailure.HostOffline, offline.Failure);
        var wrongToken = relay.Endpoint with { Token = "wrong" };
        Assert.Equal(RelayFailure.Unauthorized, (await Assert.ThrowsAsync<RelayException>(() => RelayDialer.ConnectAsync(wrongToken, host.Id))).Failure);
        var nobody = RelayEndpoint.TryCreate("http://127.0.0.1:9", "t")!;
        Assert.Equal(RelayFailure.Unreachable, (await Assert.ThrowsAsync<RelayException>(() => RelayDialer.ConnectAsync(nobody, host.Id))).Failure);

        await using var link = EchoHost(wrongToken, host, device);
        link.Start();
        await Until(() => link.State == RelayLinkState.Failed, "the host's link says it failed");
        Assert.Contains("token", link.Error);
        Assert.Empty(relay.Hosts);
    }

    [Fact]
    public async Task The_hosts_link_comes_back_after_the_relay_dropped_it()
    {
        await using var relay = new FakeRelay();
        using var host = DeviceIdentity.Create();
        using var device = DeviceIdentity.Create();
        var states = new ConcurrentQueue<RelayLinkState>();
        await using var link = EchoHost(relay.Endpoint, host, device);
        link.Changed += () => states.Enqueue(link.State);
        link.Start();
        await Until(() => link.State == RelayLinkState.Online, "the host registers at the relay");

        relay.DropHosts();

        await Until(() => relay.Registrations == 2 && link.State == RelayLinkState.Online, "the host registers again");
        Assert.Contains(RelayLinkState.Failed, states);
        Assert.Equal("echo: still here", await AskAsync(relay.Endpoint, host, device, "still here"));

        // Turned off: the host is gone from the relay.
        await link.DisposeAsync();
        await Until(() => relay.Hosts.Count == 0, $"the host left the relay (state {link.State}, error {link.Error}, registrations {relay.Registrations})");
    }

    /// <summary>A device signed in at the stand-in relay: started on the device, confirmed by the test in the browser's place.</summary>
    private static async Task<RelayTokens> SignInAsync(FakeRelay relay, DeviceIdentity device, string account = "ada")
    {
        var url = new Uri(relay.Url);
        var login = await RelayAccountClient.StartLoginAsync(url, device, "A device").WaitAsync(Limit);
        Assert.True(relay.Approve(login.UserCode, account));
        var (status, tokens) = await RelayAccountClient.PollLoginAsync(url, login, device).WaitAsync(Limit);
        Assert.Equal(RelayLoginStatus.SignedIn, status);
        return tokens!;
    }

    [Fact]
    public async Task A_device_signs_in_with_a_code_and_its_key_and_its_refresh_token_works_once()
    {
        await using var relay = new FakeRelay();
        var url = new Uri(relay.Url);
        using var device = DeviceIdentity.Create();
        using var thief = DeviceIdentity.Create();

        var login = await RelayAccountClient.StartLoginAsync(url, device, "Ada's Mac").WaitAsync(Limit);
        Assert.Matches("^[B-Z]{4}-[B-Z]{4}$", login.UserCode);
        Assert.Equal($"{relay.Url}/device?code={login.UserCode}", login.VerificationUriComplete.ToString());
        Assert.Equal(["Ada's Mac"], relay.LoginNames);
        Assert.Equal((RelayLoginStatus.Pending, null), await RelayAccountClient.PollLoginAsync(url, login, device).WaitAsync(Limit));
        // Who saw the device code but holds another key gets nothing, confirmed or not.
        Assert.True(relay.Approve(login.UserCode));
        var refused = await Assert.ThrowsAsync<RelayException>(() => RelayAccountClient.PollLoginAsync(url, login, thief));
        Assert.Equal(RelayFailure.Unauthorized, refused.Failure);

        var (status, tokens) = await RelayAccountClient.PollLoginAsync(url, login, device).WaitAsync(Limit);
        Assert.Equal(RelayLoginStatus.SignedIn, status);
        Assert.Equal(("id-ada", "Ada", "ada@example.com"), (tokens!.Account.Id, tokens.Account.Name, tokens.Account.Email));
        Assert.InRange(tokens.AccessExpiresAt, DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));
        Assert.DoesNotContain(tokens.RefreshToken, tokens.ToString());
        // The code is used up.
        Assert.Equal(RelayLoginStatus.Expired, (await RelayAccountClient.PollLoginAsync(url, login, device).WaitAsync(Limit)).Status);

        var next = await RelayAccountClient.RefreshAsync(url, tokens.RefreshToken).WaitAsync(Limit);
        Assert.NotEqual(tokens.RefreshToken, next!.RefreshToken);
        Assert.Null(await RelayAccountClient.RefreshAsync(url, tokens.RefreshToken).WaitAsync(Limit));
        await RelayAccountClient.RevokeAsync(url, next.RefreshToken).WaitAsync(Limit);
        Assert.Null(await RelayAccountClient.RefreshAsync(url, next.RefreshToken).WaitAsync(Limit));

        // A person who says no, and a relay that is not there (signing out there is then not waited for).
        var second = await RelayAccountClient.StartLoginAsync(url, device, "Ada's Mac").WaitAsync(Limit);
        Assert.True(relay.Deny(second.UserCode));
        Assert.Equal(RelayLoginStatus.Denied, (await RelayAccountClient.PollLoginAsync(url, second, device).WaitAsync(Limit)).Status);
        var nowhere = new Uri("http://127.0.0.1:9/");
        Assert.Equal(RelayFailure.Unreachable, (await Assert.ThrowsAsync<RelayException>(() => RelayAccountClient.StartLoginAsync(nowhere, device, "x"))).Failure);
        Assert.Equal(RelayFailure.Unreachable, (await Assert.ThrowsAsync<RelayException>(() => RelayAccountClient.RefreshAsync(nowhere, "or1.x"))).Failure);
        await RelayAccountClient.RevokeAsync(nowhere, "or1.x").WaitAsync(Limit);
    }

    [Fact]
    public void What_a_device_signs_to_sign_in_is_the_relays_own_hash()
    {
        // Put together here from the relay's text (03-账号与登录.md §4), not with the app's helper.
        var expected = SHA256.HashData(Encoding.ASCII.GetBytes("octopus-relay/1 login\nthe-device-code"));
        Assert.Equal(expected, RelayAccountClient.LoginHash("the-device-code"));
    }

    [Fact]
    public async Task The_relay_joins_only_devices_signed_in_to_the_same_account()
    {
        await using var relay = new FakeRelay();
        using var host = DeviceIdentity.Create();
        using var device = DeviceIdentity.Create();
        using var outsider = DeviceIdentity.Create();
        var url = new Uri(relay.Url);
        var mine = new RelayEndpoint(url, (await SignInAsync(relay, device)).AccessToken);
        var theirs = new RelayEndpoint(url, (await SignInAsync(relay, outsider, "bob")).AccessToken);
        await using var link = EchoHost(new RelayEndpoint(url, (await SignInAsync(relay, host)).AccessToken), host, device);
        link.Start();
        await Until(() => link.State == RelayLinkState.Online, "the host registers at the relay: " + link.Error);

        Assert.Equal("echo: same account", await AskAsync(mine, host, device, "same account"));
        // To another account the host is not there at all.
        Assert.Equal(RelayFailure.HostOffline, (await Assert.ThrowsAsync<RelayException>(() => RelayDialer.ConnectAsync(theirs, host.Id))).Failure);
    }

    [Fact]
    public async Task The_hosts_link_asks_for_a_new_token_when_its_own_is_refused_and_stops_when_signed_out()
    {
        await using var relay = new FakeRelay();
        using var host = DeviceIdentity.Create();
        using var device = DeviceIdentity.Create();
        var url = new Uri(relay.Url);
        var tokens = await SignInAsync(relay, host);
        var asked = new ConcurrentQueue<bool>();
        // What the app does: hand out the token at hand, and refresh when told the last one was refused.
        async Task<RelayEndpoint?> EndpointAsync(bool fresh, CancellationToken cancellationToken)
        {
            asked.Enqueue(fresh);
            if (fresh)
            {
                if (await RelayAccountClient.RefreshAsync(url, tokens.RefreshToken, cancellationToken) is not { } next) return null;
                tokens = next;
            }
            return new RelayEndpoint(url, tokens.AccessToken);
        }
        await using var link = new RelayHostLink(EndpointAsync, host, () => Station.Name, _ => Task.CompletedTask) { RetryPause = TimeSpan.FromMilliseconds(50) };
        link.Start();
        await Until(() => link.State == RelayLinkState.Online, "the host registers at the relay: " + link.Error);
        Assert.Equal([false], asked);

        // The token's time is up and the connection drops: refused once, then back with a new token.
        relay.ExpireAccessTokens();
        relay.DropHosts();
        await Until(() => relay.Registrations == 2 && link.State == RelayLinkState.Online, "the host registers again: " + link.Error);
        Assert.Equal([false, false, true], asked);
        Assert.Equal(1, relay.Refreshes);

        // Removed from the account: the relay says so, no new token is to be had, and the link says why it is down.
        relay.SignOut(host.Id);
        await Until(() => link.State == RelayLinkState.Failed && link.Error!.Contains("not signed in", StringComparison.Ordinal), "the link says this computer is signed out: " + link.Error);
        Assert.Empty(relay.Hosts);
        Assert.Equal(2, relay.Registrations);
    }

    /// <summary>
    /// The same exchange against a real relay service, when one is named: run OctopusServer with <c>--dev</c> and set
    /// OCTOPUS_RELAY_TEST_URL. The two devices sign in the way people do (a code, confirmed in a browser, played here
    /// by plain requests under the development sign-in). Without the variable the test has nothing to check.
    /// </summary>
    [Fact]
    public async Task A_real_relay_service_signs_devices_in_and_carries_the_same_exchange()
    {
        var url = RelayAccountClient.ParseUrl(Environment.GetEnvironmentVariable("OCTOPUS_RELAY_TEST_URL"));
        Assert.SkipWhen(url is null, "No relay service is named (OCTOPUS_RELAY_TEST_URL, a service started with --dev).");
        using var host = DeviceIdentity.Create();
        using var device = DeviceIdentity.Create();
        using var outsider = DeviceIdentity.Create();
        var account = "ada-" + Guid.NewGuid().ToString("N")[..8];

        var hostTokens = await SignInAtRealAsync(url!, host, account);
        var deviceTokens = await SignInAtRealAsync(url!, device, account);
        var outsiderTokens = await SignInAtRealAsync(url!, outsider, account + "-other");
        Assert.Equal(account, hostTokens.Account.Name);
        Assert.Equal(hostTokens.Account.Id, deviceTokens.Account.Id);
        Assert.NotEqual(hostTokens.Account.Id, outsiderTokens.Account.Id);

        var relay = new RelayEndpoint(url!, deviceTokens.AccessToken);
        await using var link = EchoHost(new RelayEndpoint(url!, hostTokens.AccessToken), host, device);
        link.Start();
        await Until(() => link.State == RelayLinkState.Online, "the host registers at the relay: " + link.Error);

        Assert.Equal("echo: through the real one", await AskAsync(relay, host, device, "through the real one"));
        var large = new string('x', 900_000);
        Assert.Equal("echo: " + large, await AskAsync(relay, host, device, large));
        using var unknown = DeviceIdentity.Create();
        Assert.Equal(RelayFailure.HostOffline, (await Assert.ThrowsAsync<RelayException>(() => RelayDialer.ConnectAsync(relay, unknown.Id))).Failure);
        Assert.Equal(RelayFailure.Unauthorized, (await Assert.ThrowsAsync<RelayException>(() => RelayDialer.ConnectAsync(relay with { Token = "not-the-token" }, host.Id))).Failure);
        // Another account does not get to the host, signed in though it is.
        Assert.Equal(RelayFailure.HostOffline, (await Assert.ThrowsAsync<RelayException>(() => RelayDialer.ConnectAsync(new RelayEndpoint(url!, outsiderTokens.AccessToken), host.Id))).Failure);

        // The account's devices as the phone lists them: the two of this account, the host online, the asker marked.
        var devices = await RelayAccountClient.ListDevicesAsync(relay).WaitAsync(Limit);
        Assert.Equal(new[] { host.Id, device.Id }.Order(), devices.Select(listed => listed.Id).Order());
        Assert.Equal((true, false, "desktop"), devices.Single(listed => listed.Id == host.Id) is var hostEntry ? (hostEntry.Online, hostEntry.Current, hostEntry.Kind) : default);
        Assert.Equal((false, true), devices.Single(listed => listed.Id == device.Id) is var own ? (own.Online, own.Current) : default);
        Assert.Equal(RelayFailure.Unauthorized, (await Assert.ThrowsAsync<RelayException>(() => RelayAccountClient.ListDevicesAsync(relay with { Token = "not-the-token" }))).Failure);

        // The refresh token works once; signing out ends the sign-in and the host's registration with it.
        var next = await RelayAccountClient.RefreshAsync(url!, hostTokens.RefreshToken).WaitAsync(Limit);
        Assert.NotNull(next);
        await RelayAccountClient.RevokeAsync(url!, next.RefreshToken).WaitAsync(Limit);
        Assert.Null(await RelayAccountClient.RefreshAsync(url!, next.RefreshToken).WaitAsync(Limit));
        await Until(() => link.State == RelayLinkState.Failed, "the host's link is down after signing out");
        Assert.Equal(RelayFailure.HostOffline, (await Assert.ThrowsAsync<RelayException>(() => RelayDialer.ConnectAsync(relay, host.Id))).Failure);
    }

    /// <summary>Signs a device in at a real service in development mode: the device's part with the app's client, the person's part by hand.</summary>
    private static async Task<RelayTokens> SignInAtRealAsync(Uri url, DeviceIdentity device, string account)
    {
        var login = await RelayAccountClient.StartLoginAsync(url, device, "Test device").WaitAsync(Limit);
        using var browser = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = url };
        browser.DefaultRequestHeaders.Add("X-Octopus-Request", "1");
        using (var signedIn = await browser.GetAsync($"v1/auth/login/dev?name={account}").WaitAsync(Limit))
        {
            Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        }
        using (var approved = await browser.PostAsync($"v1/auth/device/{login.UserCode}", new StringContent("""{"approve":true}""", Encoding.UTF8, "application/json")).WaitAsync(Limit))
        {
            Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        }
        var (status, tokens) = await RelayAccountClient.PollLoginAsync(url, login, device).WaitAsync(Limit);
        Assert.Equal(RelayLoginStatus.SignedIn, status);
        return tokens!;
    }
}
