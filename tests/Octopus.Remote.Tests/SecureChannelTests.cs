using System.Security.Cryptography;
using System.Text;
using Octopus.Remote.Security;
using Octopus.Remote.Transport;
using Xunit;

namespace Octopus.Remote.Tests;

/// <summary>A host's door for the tests: a list of paired devices, an optional pairing window, and a record of who knocked.</summary>
internal sealed class Door : IAcceptPolicy
{
    public HashSet<DevicePublicKey> Paired { get; } = [];
    public bool PairingOpen { get; set; }
    public byte[]? Token { get; set; }
    /// <summary>The person's answer to a request to compare numbers; null leaves the request waiting.</summary>
    public TaskCompletionSource<bool> Person { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<PeerOffer> Offers { get; } = [];

    public AcceptDecision Decide(PeerOffer offer)
    {
        Offers.Add(offer);
        if (Paired.Contains(offer.Key)) return AcceptDecision.Accept;
        if (!offer.RequestsPairing) return AcceptDecision.Reject(HandshakeStatus.UnknownDevice);
        if (!PairingOpen) return AcceptDecision.Reject(HandshakeStatus.PairingClosed);
        if (Token is { } token && offer.Token.Length > 0 && CryptographicOperations.FixedTimeEquals(token, offer.Token.Span))
        {
            Token = null; // once
            Paired.Add(offer.Key);
            return AcceptDecision.Accept;
        }
        return AcceptDecision.AskPerson(Person.Task);
    }
}

public class SecureChannelTests
{
    private static readonly DeviceDescription Laptop = new("Laptop", DeviceKind.Desktop);
    private static readonly DeviceDescription Station = new("工作站", DeviceKind.Desktop);
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private static async Task<(SecureChannel Client, SecureChannel Host)> Connect(DeviceIdentity client, DeviceIdentity host, Door door, PeerExpectation? expected = null, ConnectOptions? options = null)
    {
        var (a, b) = InMemoryTransport.CreatePair();
        var accepting = SecureChannel.AcceptAsync(b, host, Station, door);
        var connecting = SecureChannel.ConnectAsync(a, client, Laptop, expected ?? PeerExpectation.Paired(host.PublicKey), options);
        return (await connecting.WaitAsync(Limit), await accepting.WaitAsync(Limit));
    }

    [Fact]
    public async Task Paired_devices_get_a_channel_that_carries_messages_both_ways()
    {
        using var client = DeviceIdentity.Create();
        using var host = DeviceIdentity.Create();
        var door = new Door { Paired = { client.PublicKey } };

        var (c, h) = await Connect(client, host, door);
        await using var _ = c;
        await using var __ = h;

        Assert.Equal(host.PublicKey, c.Peer);
        Assert.Equal(client.PublicKey, h.Peer);
        Assert.Equal(Station, c.PeerDescription);
        Assert.Equal(Laptop, h.PeerDescription);
        Assert.Equal(c.Sas, h.Sas);
        Assert.Matches("^[0-9]{6}$", c.Sas);
        Assert.False(c.WasPaired);

        await c.SendAsync("ls -la\r"u8.ToArray());
        await c.SendAsync(Array.Empty<byte>());
        await h.SendAsync(Encoding.UTF8.GetBytes("total 42 中文"));
        Assert.Equal("ls -la\r", Encoding.UTF8.GetString((await h.ReceiveAsync())!));
        Assert.Empty((await h.ReceiveAsync())!);
        Assert.Equal("total 42 中文", Encoding.UTF8.GetString((await c.ReceiveAsync())!));

        // The largest message fits; one byte more is refused before anything is sent.
        var big = RandomNumberGenerator.GetBytes(SecureChannel.MaxMessageLength);
        await c.SendAsync(big);
        Assert.Equal(big, await h.ReceiveAsync());
        await Assert.ThrowsAsync<ArgumentException>(async () => await c.SendAsync(new byte[SecureChannel.MaxMessageLength + 1]));

        await c.DisposeAsync();
        Assert.Null(await h.ReceiveAsync());
    }

    [Fact]
    public async Task What_travels_is_not_the_plaintext()
    {
        using var client = DeviceIdentity.Create();
        using var host = DeviceIdentity.Create();
        var (a, b) = InMemoryTransport.CreatePair();
        var seen = new List<byte[]>();
        a.Outgoing = message => { lock (seen) seen.Add(message); return message; };
        var accepting = SecureChannel.AcceptAsync(b, host, Station, new Door { Paired = { client.PublicKey } });
        await using var c = await SecureChannel.ConnectAsync(a, client, Laptop, PeerExpectation.Paired(host.PublicKey));
        await using var h = await accepting;

        await c.SendAsync("the secret prompt"u8.ToArray());
        await h.ReceiveAsync();

        var wire = seen.SelectMany(message => message).ToArray();
        Assert.Equal(-1, wire.AsSpan().IndexOf("the secret prompt"u8));
        // Neither the device's name nor its key went out in the clear.
        Assert.Equal(-1, wire.AsSpan().IndexOf("Laptop"u8));
        Assert.Equal(-1, wire.AsSpan().IndexOf(client.PublicKey.Bytes));
    }

    [Fact]
    public async Task A_device_the_host_does_not_know_is_turned_away()
    {
        using var client = DeviceIdentity.Create();
        using var host = DeviceIdentity.Create();
        var door = new Door();
        var (a, b) = InMemoryTransport.CreatePair();
        var accepting = SecureChannel.AcceptAsync(b, host, Station, door);

        var refused = await Assert.ThrowsAsync<HandshakeException>(() => SecureChannel.ConnectAsync(a, client, Laptop, PeerExpectation.Paired(host.PublicKey)));
        Assert.Equal(HandshakeFailure.Rejected, refused.Failure);
        Assert.Equal(HandshakeStatus.UnknownDevice, refused.Status);
        Assert.Equal(HandshakeStatus.UnknownDevice, (await Assert.ThrowsAsync<HandshakeException>(() => accepting)).Status);

        // Asking to be paired does not help while the host is not pairing.
        (a, b) = InMemoryTransport.CreatePair();
        accepting = SecureChannel.AcceptAsync(b, host, Station, door);
        refused = await Assert.ThrowsAsync<HandshakeException>(() => SecureChannel.ConnectAsync(a, client, Laptop,
            PeerExpectation.Paired(host.PublicKey), new ConnectOptions { RequestPairing = true }));
        Assert.Equal(HandshakeStatus.PairingClosed, refused.Status);
        await Assert.ThrowsAsync<HandshakeException>(() => accepting);
    }

    [Fact]
    public async Task The_client_does_not_show_itself_to_a_host_with_another_key()
    {
        using var client = DeviceIdentity.Create();
        using var host = DeviceIdentity.Create();
        using var impostor = DeviceIdentity.Create();
        var door = new Door { Paired = { client.PublicKey } };
        var (a, b) = InMemoryTransport.CreatePair();
        var sent = 0;
        a.Outgoing = message => { sent++; return message; };
        var accepting = SecureChannel.AcceptAsync(b, impostor, Station, door);

        var wrong = await Assert.ThrowsAsync<HandshakeException>(() => SecureChannel.ConnectAsync(a, client, Laptop, PeerExpectation.Paired(host.PublicKey)));

        Assert.Equal(HandshakeFailure.WrongPeer, wrong.Failure);
        // Only the commitment went out: no key, no name.
        Assert.Equal(1, sent);
        Assert.Empty(door.Offers);
        Assert.Equal(HandshakeFailure.Closed, (await Assert.ThrowsAsync<HandshakeException>(() => accepting)).Failure);
    }

    [Fact]
    public async Task Pairing_by_comparing_numbers_waits_for_the_person_at_the_host()
    {
        using var client = DeviceIdentity.Create();
        using var host = DeviceIdentity.Create();
        var door = new Door { PairingOpen = true };
        var (a, b) = InMemoryTransport.CreatePair();
        var shown = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepting = SecureChannel.AcceptAsync(b, host, Station, door);
        var connecting = SecureChannel.ConnectAsync(a, client, Laptop, PeerExpectation.AnyForPairing,
            new ConnectOptions { RequestPairing = true, OnPending = shown.SetResult });

        // Both sides show the same six digits; nothing is established until the person allows it.
        var clientDigits = await shown.Task.WaitAsync(Limit);
        Assert.Equal(door.Offers.Single().Sas, clientDigits);
        Assert.Equal("Laptop", door.Offers[0].Description.Name);
        Assert.False(connecting.IsCompleted);

        door.Person.SetResult(true);
        await using var c = await connecting.WaitAsync(Limit);
        await using var h = await accepting.WaitAsync(Limit);
        Assert.True(c.WasPaired);
        Assert.True(h.WasPaired);
        Assert.Equal(host.PublicKey, c.Peer);
        await c.SendAsync("hi"u8.ToArray());
        Assert.Equal("hi"u8.ToArray(), await h.ReceiveAsync());
    }

    [Fact]
    public async Task A_refused_pairing_ends_the_connection_on_both_sides()
    {
        using var client = DeviceIdentity.Create();
        using var host = DeviceIdentity.Create();
        var door = new Door { PairingOpen = true };
        var (a, b) = InMemoryTransport.CreatePair();
        var accepting = SecureChannel.AcceptAsync(b, host, Station, door);
        var connecting = SecureChannel.ConnectAsync(a, client, Laptop, PeerExpectation.AnyForPairing, new ConnectOptions { RequestPairing = true });

        door.Person.SetResult(false);

        Assert.Equal(HandshakeStatus.PairingRefused, (await Assert.ThrowsAsync<HandshakeException>(() => connecting.WaitAsync(Limit))).Status);
        Assert.Equal(HandshakeStatus.PairingRefused, (await Assert.ThrowsAsync<HandshakeException>(() => accepting.WaitAsync(Limit))).Status);
    }

    [Fact]
    public async Task A_pairing_link_pairs_at_once_and_its_token_works_one_time()
    {
        using var client = DeviceIdentity.Create();
        using var other = DeviceIdentity.Create();
        using var host = DeviceIdentity.Create();
        var token = RandomNumberGenerator.GetBytes(16);
        var door = new Door { PairingOpen = true, Token = token };
        var link = new ConnectOptions { RequestPairing = true, Token = token };

        var (c, h) = await Connect(client, host, door, PeerExpectation.WithFingerprint(host.PublicKey.Fingerprint()), link);
        Assert.True(c.WasPaired);
        Assert.Contains(client.PublicKey, door.Paired);
        await c.DisposeAsync();
        await h.DisposeAsync();

        // The same link in other hands: the token is spent, so that device has to be allowed by a person.
        var (a, b) = InMemoryTransport.CreatePair();
        var accepting = SecureChannel.AcceptAsync(b, host, Station, door);
        var connecting = SecureChannel.ConnectAsync(a, other, Laptop, PeerExpectation.WithFingerprint(host.PublicKey.Fingerprint()), link);
        door.Person.SetResult(false);
        await Assert.ThrowsAsync<HandshakeException>(() => connecting.WaitAsync(Limit));
        await Assert.ThrowsAsync<HandshakeException>(() => accepting.WaitAsync(Limit));
        Assert.DoesNotContain(other.PublicKey, door.Paired);

        // A link that names another host is not followed.
        (a, b) = InMemoryTransport.CreatePair();
        accepting = SecureChannel.AcceptAsync(b, host, Station, door);
        var wrong = await Assert.ThrowsAsync<HandshakeException>(() => SecureChannel.ConnectAsync(a, client, Laptop,
            PeerExpectation.WithFingerprint(other.PublicKey.Fingerprint()), link));
        Assert.Equal(HandshakeFailure.WrongPeer, wrong.Failure);
        await Assert.ThrowsAsync<HandshakeException>(() => accepting);
    }

    /// <summary>
    /// Somebody in the middle runs one handshake with each side under keys of their own. Each handshake succeeds by
    /// itself (pairing accepts any key), but the two sides show different numbers: that is what the people compare.
    /// </summary>
    [Fact]
    public async Task Somebody_in_the_middle_makes_the_two_sides_show_different_numbers()
    {
        using var client = DeviceIdentity.Create();
        using var host = DeviceIdentity.Create();
        using var middle = DeviceIdentity.Create();
        var hostDoor = new Door { PairingOpen = true };
        var middleDoor = new Door { PairingOpen = true };

        // Client <-> middle (who plays the host).
        var (a, b) = InMemoryTransport.CreatePair();
        var clientDigits = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = SecureChannel.AcceptAsync(b, middle, Station, middleDoor);
        _ = SecureChannel.ConnectAsync(a, client, Laptop, PeerExpectation.AnyForPairing, new ConnectOptions { RequestPairing = true, OnPending = clientDigits.SetResult });
        // Middle (who plays the client) <-> host.
        var (x, y) = InMemoryTransport.CreatePair();
        _ = SecureChannel.AcceptAsync(y, host, Station, hostDoor);
        _ = SecureChannel.ConnectAsync(x, middle, Laptop, PeerExpectation.AnyForPairing, new ConnectOptions { RequestPairing = true });

        var onClient = await clientDigits.Task.WaitAsync(Limit);
        for (var i = 0; i < 500 && hostDoor.Offers.Count == 0; i++) await Task.Delay(10);
        var onHost = hostDoor.Offers.Single().Sas;

        // One in a million would be equal by chance; the host also sees the middle's key, not the client's.
        Assert.NotEqual(onClient, onHost);
        Assert.Equal(middle.PublicKey, hostDoor.Offers[0].Key);
        middleDoor.Person.SetResult(false);
        hostDoor.Person.SetResult(false);
    }

    [Fact]
    public async Task A_changed_dropped_or_repeated_record_ends_the_channel()
    {
        foreach (var attack in new[] { "flip", "drop", "repeat" })
        {
            using var client = DeviceIdentity.Create();
            using var host = DeviceIdentity.Create();
            var (a, b) = InMemoryTransport.CreatePair();
            var accepting = SecureChannel.AcceptAsync(b, host, Station, new Door { Paired = { client.PublicKey } });
            await using var c = await SecureChannel.ConnectAsync(a, client, Laptop, PeerExpectation.Paired(host.PublicKey));
            await using var h = await accepting;
            await c.SendAsync("first"u8.ToArray());
            Assert.Equal("first"u8.ToArray(), await h.ReceiveAsync());

            byte[]? last = null;
            a.Outgoing = attack switch
            {
                "flip" => message => { message[0] ^= 1; return message; }
                ,
                "drop" => message => { if (last is null) { last = message; return null; } return message; }
                ,
                _ => message => message,
            };
            if (attack == "repeat")
            {
                byte[]? captured = null;
                a.Outgoing = message => captured = message;
                await c.SendAsync("second"u8.ToArray());
                Assert.Equal("second"u8.ToArray(), await h.ReceiveAsync());
                a.Outgoing = null;
                await a.SendAsync(captured); // the network plays it again
            }
            else
            {
                await c.SendAsync("second"u8.ToArray());
                if (attack == "drop") await c.SendAsync("third"u8.ToArray());
            }

            var broken = await Assert.ThrowsAsync<HandshakeException>(async () => await h.ReceiveAsync());
            Assert.Equal(HandshakeFailure.Authentication, broken.Failure);
        }
    }

    [Fact]
    public async Task Handshake_messages_that_were_changed_on_the_way_are_caught()
    {
        // Each handshake message of the client, with one byte flipped somewhere in it.
        for (var index = 0; index < 2; index++)
        {
            using var client = DeviceIdentity.Create();
            using var host = DeviceIdentity.Create();
            var (a, b) = InMemoryTransport.CreatePair();
            var sent = 0;
            var target = index;
            a.Outgoing = message =>
            {
                if (sent++ == target) message[^1] ^= 0x40;
                return message;
            };
            var accepting = SecureChannel.AcceptAsync(b, host, Station, new Door { Paired = { client.PublicKey } });
            var connecting = SecureChannel.ConnectAsync(a, client, Laptop, PeerExpectation.Paired(host.PublicKey));

            var hostSide = await Assert.ThrowsAsync<HandshakeException>(() => accepting.WaitAsync(Limit));
            var clientSide = await Assert.ThrowsAsync<HandshakeException>(() => connecting.WaitAsync(Limit));
            // The first message is covered by the host's signature: the client, who knows what it sent, catches the
            // change. The third carries the client's proof under the new key: the host catches that one. The other
            // side only sees the connection end.
            var (catching, other) = index == 0 ? (clientSide, hostSide) : (hostSide, clientSide);
            Assert.Equal(HandshakeFailure.Authentication, catching.Failure);
            Assert.Equal(HandshakeFailure.Closed, other.Failure);
        }

        // The host's reply with its signature damaged: the client stops there.
        {
            using var client = DeviceIdentity.Create();
            using var host = DeviceIdentity.Create();
            var (a, b) = InMemoryTransport.CreatePair();
            b.Outgoing = message => { message[^1] ^= 0x40; return message; };
            var accepting = SecureChannel.AcceptAsync(b, host, Station, new Door { Paired = { client.PublicKey } });
            var stopped = await Assert.ThrowsAsync<HandshakeException>(() => SecureChannel.ConnectAsync(a, client, Laptop, PeerExpectation.Paired(host.PublicKey)));
            Assert.Equal(HandshakeFailure.Authentication, stopped.Failure);
            await Assert.ThrowsAsync<HandshakeException>(() => accepting.WaitAsync(Limit));
        }
    }

    [Fact]
    public async Task Garbage_and_oversized_first_messages_are_refused()
    {
        using var host = DeviceIdentity.Create();
        foreach (var first in new[] { new byte[] { 0x01 }, new byte[35], [0x01, 9, 0, .. new byte[32]], [0x01, 1, 0x80, .. new byte[32]], new byte[SecureChannel.MaxHandshakeLength + 1] })
        {
            var (a, b) = InMemoryTransport.CreatePair();
            var accepting = SecureChannel.AcceptAsync(b, host, Station, new Door());
            await a.SendAsync(first);
            Assert.Equal(HandshakeFailure.Protocol, (await Assert.ThrowsAsync<HandshakeException>(() => accepting.WaitAsync(Limit))).Failure);
        }
    }

    [Fact]
    public async Task A_point_that_is_not_on_the_curve_is_not_a_key()
    {
        var point = new byte[65];
        point[0] = 0x04;
        point[32] = 1;
        point[64] = 1;
        Assert.ThrowsAny<CryptographicException>(() => DevicePublicKey.Parse(point));
        Assert.ThrowsAny<CryptographicException>(() => DevicePublicKey.Parse(new byte[64]));

        // As the host's ephemeral key it ends the handshake instead of being used.
        using var client = DeviceIdentity.Create();
        using var host = DeviceIdentity.Create();
        var (a, b) = InMemoryTransport.CreatePair();
        b.Outgoing = message =>
        {
            if (message[0] == 0x02) point.CopyTo(message, 2);
            return message;
        };
        var accepting = SecureChannel.AcceptAsync(b, host, Station, new Door { Paired = { client.PublicKey } });
        await Assert.ThrowsAsync<HandshakeException>(() => SecureChannel.ConnectAsync(a, client, Laptop, PeerExpectation.Paired(host.PublicKey)));
        await Assert.ThrowsAsync<HandshakeException>(() => accepting.WaitAsync(Limit));
    }

    [Fact]
    public void An_identity_survives_the_keystore_and_names_its_device()
    {
        using var identity = DeviceIdentity.Create();
        using var restored = DeviceIdentity.Import(identity.Export());

        Assert.Equal(identity.PublicKey, restored.PublicKey);
        Assert.Equal(identity.Id, restored.Id);
        Assert.Matches("^[a-z2-7]{26}$", identity.Id);
        Assert.Equal(65, identity.PublicKey.Bytes.Length);
        Assert.Equal(DevicePublicKey.Parse(identity.PublicKey.Bytes), identity.PublicKey);

        using var another = DeviceIdentity.Create();
        Assert.NotEqual(identity.PublicKey, another.PublicKey);
        Assert.NotEqual(identity.Id, another.Id);
        // Base32 as in RFC 4648 (its test vectors, in lower case and without padding).
        Assert.Equal("mzxw6ytboi", Base32.Encode("foobar"u8));
        Assert.Equal("my", Base32.Encode("f"u8));
    }

    [Fact]
    public async Task A_long_name_is_cut_at_a_character_and_still_arrives()
    {
        using var client = DeviceIdentity.Create();
        using var host = DeviceIdentity.Create();
        var (a, b) = InMemoryTransport.CreatePair();
        var door = new Door { Paired = { client.PublicKey } };
        var accepting = SecureChannel.AcceptAsync(b, host, Station, door);
        var name = string.Concat(Enumerable.Repeat("书房的电脑", 10)); // 150 bytes in UTF-8
        await using var c = await SecureChannel.ConnectAsync(a, client, new DeviceDescription(name, DeviceKind.Phone), PeerExpectation.Paired(host.PublicKey));
        await using var h = await accepting;

        Assert.Equal(DeviceKind.Phone, h.PeerDescription.Kind);
        Assert.StartsWith(h.PeerDescription.Name, name);
        Assert.Equal(63, Encoding.UTF8.GetByteCount(h.PeerDescription.Name)); // 21 whole characters
    }
}

/// <summary>Fixed inputs give fixed outputs: the labels and layouts of design/remote/安全通道.md, pinned.</summary>
public class SecureChannelVectorTests
{
    [Fact]
    public void The_six_digits_come_from_the_transcript_and_the_initiators_key()
    {
        var transcript = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var identity = Enumerable.Range(0, 65).Select(i => (byte)(0x40 + i)).ToArray();

        var digits = SecureChannel.ComputeSas(transcript, identity);

        // SHA-256("octopus-remote/1 sas" ‖ transcript ‖ identity), first four bytes big-endian, mod 1 000 000.
        var hash = System.Security.Cryptography.SHA256.HashData([.. "octopus-remote/1 sas"u8, .. transcript, .. identity]);
        var expected = (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000).ToString("D6");
        Assert.Equal(expected, digits);
        // The same computed apart from this code base (Python's hashlib).
        Assert.Equal("910527", digits);
        // One bit of either input changes them.
        transcript[0] ^= 1;
        Assert.NotEqual(digits, SecureChannel.ComputeSas(transcript, identity));
    }
}
