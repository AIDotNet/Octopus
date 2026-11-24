using Octopus.Remote.Security;
using Octopus.Remote.Transport;
using Xunit;

namespace Octopus.Remote.Tests;

/// <summary>A host's door that takes devices with an access password (design/remote/安全通道.md §3.3).</summary>
internal sealed class PasswordDoor(byte[] key) : IAcceptPolicy
{
    public List<PeerOffer> Offers { get; } = [];

    public AcceptDecision Decide(PeerOffer offer)
    {
        Offers.Add(offer);
        return offer.ProvesPassword(key) ? AcceptDecision.AcceptWithPassword(key) : AcceptDecision.Reject(HandshakeStatus.WrongPassword);
    }
}

/// <summary>Somebody who answers in the host's place (the relay could): lets everyone in, with a key of its own choosing.</summary>
internal sealed class ImpostorDoor(byte[]? key) : IAcceptPolicy
{
    public AcceptDecision Decide(PeerOffer offer) => key is null ? AcceptDecision.Accept : AcceptDecision.AcceptWithPassword(key);
}

public class AccessPasswordTests
{
    private static readonly DeviceDescription Phone = new("Phone", DeviceKind.Phone);
    private static readonly DeviceDescription Station = new("工作站", DeviceKind.Desktop);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    private static async Task<(SecureChannel Client, SecureChannel Host)> Connect(DeviceIdentity client, DeviceIdentity host, IAcceptPolicy door, string password, PeerExpectation? expected = null)
    {
        var (a, b) = InMemoryTransport.CreatePair();
        var accepting = SecureChannel.AcceptAsync(b, host, Station, door);
        var connecting = SecureChannel.ConnectAsync(a, client, Phone, expected ?? PeerExpectation.WithDeviceId(host.Id), new ConnectOptions { Password = password });
        // The host's side finishes or fails with the client's; whichever throws first is what the test sees.
        var channel = await connecting.WaitAsync(Limit);
        return (channel, await accepting.WaitAsync(Limit));
    }

    [Theory]
    [InlineData("abcd-efgh", "ABCDEFGH")]
    [InlineData(" AbCd EfGh ", "ABCDEFGH")]
    [InlineData("12345678", "12345678")]
    [InlineData("ABCD-EFG", null)]
    [InlineData("ABCDEFGHJ", null)]
    [InlineData("ABCD_EFGH", null)]
    [InlineData("ABCDEFGé", null)]
    [InlineData("", null)]
    public void A_password_is_eight_letters_and_digits_whatever_the_case_and_dashes(string typed, string? normalized)
    {
        Assert.Equal(normalized, AccessPassword.Normalize(typed));
    }

    [Fact]
    public void Made_passwords_use_only_characters_people_do_not_mix_up()
    {
        for (var i = 0; i < 200; i++)
        {
            var password = AccessPassword.Generate();
            Assert.Equal(AccessPassword.Length, password.Length);
            Assert.Equal(password, AccessPassword.Normalize(password));
            Assert.All(password, c => Assert.Contains(c, AccessPassword.Alphabet));
            Assert.DoesNotContain(password, c => c is '0' or 'O' or '1' or 'I' or 'L');
        }
        Assert.Equal("ABCD-EFGH", AccessPassword.Format("ABCDEFGH"));
    }

    [Fact]
    public void The_key_belongs_to_one_host()
    {
        using var one = DeviceIdentity.Create();
        using var other = DeviceIdentity.Create();
        Assert.Equal(AccessPassword.DeriveKey("ABCDEFGH", one.PublicKey), AccessPassword.DeriveKey("ABCDEFGH", one.PublicKey));
        Assert.NotEqual(AccessPassword.DeriveKey("ABCDEFGH", one.PublicKey), AccessPassword.DeriveKey("ABCDEFGH", other.PublicKey));
        Assert.NotEqual(AccessPassword.DeriveKey("ABCDEFGH", one.PublicKey), AccessPassword.DeriveKey("ABCDEFGJ", one.PublicKey));
        Assert.Throws<ArgumentException>(() => AccessPassword.DeriveKey("abcdefgh", one.PublicKey));
    }

    [Fact]
    public async Task The_right_password_lets_the_device_in_and_both_sides_remember_each_other()
    {
        using var client = DeviceIdentity.Create();
        using var host = DeviceIdentity.Create();
        var door = new PasswordDoor(AccessPassword.DeriveKey("K7PQ2M9X", host.PublicKey));

        var (c, h) = await Connect(client, host, door, "k7pq-2m9x");
        await using var _ = c;
        await using var __ = h;

        Assert.True(c.WasPaired);
        Assert.True(h.WasPaired);
        Assert.Equal(host.PublicKey, c.Peer);
        Assert.Equal(client.PublicKey, h.Peer);
        var offer = Assert.Single(door.Offers);
        Assert.True(offer.RequestsPasswordAccess);
        Assert.False(offer.RequestsPairing);

        await c.SendAsync("pwd\r"u8.ToArray());
        Assert.Equal("pwd\r"u8.ToArray(), await h.ReceiveAsync());
    }

    [Fact]
    public async Task A_wrong_password_is_turned_away()
    {
        using var client = DeviceIdentity.Create();
        using var host = DeviceIdentity.Create();
        var door = new PasswordDoor(AccessPassword.DeriveKey("K7PQ2M9X", host.PublicKey));

        var e = await Assert.ThrowsAsync<HandshakeException>(() => Connect(client, host, door, "K7PQ2M9Y"));
        Assert.Equal(HandshakeFailure.Rejected, e.Failure);
        Assert.Equal(HandshakeStatus.WrongPassword, e.Status);
    }

    /// <summary>
    /// Somebody in the host's place (a relay that hands out another key for the host) lets the device in without
    /// knowing the password: the device notices, because that somebody cannot prove the password back.
    /// </summary>
    [Fact]
    public async Task A_host_that_cannot_prove_the_password_is_not_taken()
    {
        using var client = DeviceIdentity.Create();
        using var impostor = DeviceIdentity.Create();

        // Accepts without any proof.
        var plain = await Assert.ThrowsAsync<HandshakeException>(() => Connect(client, impostor, new ImpostorDoor(null), "K7PQ2M9X", PeerExpectation.WithDeviceId(impostor.Id)));
        Assert.Equal(HandshakeFailure.Protocol, plain.Failure); // its answer is missing the proof

        // Accepts with a proof of a password it guessed.
        var guessed = await Assert.ThrowsAsync<HandshakeException>(() => Connect(client, impostor, new ImpostorDoor(AccessPassword.DeriveKey("AAAAAAAA", impostor.PublicKey)), "K7PQ2M9X", PeerExpectation.WithDeviceId(impostor.Id)));
        Assert.Equal(HandshakeFailure.WrongPeer, guessed.Failure);
    }

    [Fact]
    public async Task The_device_only_talks_to_the_host_the_list_named()
    {
        using var client = DeviceIdentity.Create();
        using var host = DeviceIdentity.Create();
        using var other = DeviceIdentity.Create();
        var door = new PasswordDoor(AccessPassword.DeriveKey("K7PQ2M9X", host.PublicKey));

        var e = await Assert.ThrowsAsync<HandshakeException>(() => Connect(client, host, door, "K7PQ2M9X", PeerExpectation.WithDeviceId(other.Id)));
        Assert.Equal(HandshakeFailure.WrongPeer, e.Failure);
        Assert.Empty(door.Offers); // the device did not even show itself
    }

    /// <summary>A proof from one handshake is worth nothing in another: it is bound to that handshake's transcript.</summary>
    [Fact]
    public async Task A_proof_cannot_be_carried_over_to_another_handshake()
    {
        using var client = DeviceIdentity.Create();
        using var host = DeviceIdentity.Create();
        var key = AccessPassword.DeriveKey("K7PQ2M9X", host.PublicKey);
        var first = new PasswordDoor(key);
        var (c1, h1) = await Connect(client, host, first, "K7PQ2M9X");
        await c1.DisposeAsync();
        await h1.DisposeAsync();
        var second = new PasswordDoor(key);
        var (c2, h2) = await Connect(client, host, second, "K7PQ2M9X");
        await c2.DisposeAsync();
        await h2.DisposeAsync();

        var replayed = second.Offers[0] with { Token = first.Offers[0].Token };
        Assert.False(replayed.ProvesPassword(key));
        Assert.True(second.Offers[0].ProvesPassword(key));
    }

    [Fact]
    public async Task Something_that_is_not_a_password_is_not_sent()
    {
        using var client = DeviceIdentity.Create();
        using var host = DeviceIdentity.Create();
        var (a, _) = InMemoryTransport.CreatePair();
        await Assert.ThrowsAsync<ArgumentException>(() => SecureChannel.ConnectAsync(a, client, Phone, PeerExpectation.WithDeviceId(host.Id), new ConnectOptions { Password = "short" }));
    }
}
