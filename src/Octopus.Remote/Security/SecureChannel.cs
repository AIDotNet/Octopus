using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Octopus.Remote.Transport;
using static Octopus.Remote.Security.CryptoPrimitives;

namespace Octopus.Remote.Security;

/// <summary>
/// An authenticated, encrypted pipe of messages between two devices, on top of a transport nobody trusts. Both ends
/// prove their device keys in a handshake of four messages, then every message is one AES-256-GCM record. The exact
/// bytes are in design/remote/安全通道.md; this file follows that document section by section.
/// </summary>
public sealed class SecureChannel : IAsyncDisposable
{
    /// <summary>The largest message the channel carries.</summary>
    public const int MaxMessageLength = 1 << 20;
    /// <summary>The largest handshake message: nothing bigger is read from a peer that has not proven who it is.</summary>
    public const int MaxHandshakeLength = 16 * 1024;
    public const byte Version = 1;
    public const int MaxTokenLength = 32;

    private const byte TypeHello = 0x01, TypeReply = 0x02, TypeReveal = 0x03, TypeResult = 0x04;
    private const byte FlagPairing = 0x01;

    private static readonly byte[] LabelCommit = Label("octopus-remote/1 commit");
    private static readonly byte[] LabelAuthResponder = Label("octopus-remote/1 auth responder");
    private static readonly byte[] LabelAuthInitiator = Label("octopus-remote/1 auth initiator");
    private static readonly byte[] LabelTranscript = Label("octopus-remote/1 transcript");
    private static readonly byte[] LabelInitiatorKey = Label("octopus-remote/1 key i2r");
    private static readonly byte[] LabelResponderKey = Label("octopus-remote/1 key r2i");
    private static readonly byte[] LabelSas = Label("octopus-remote/1 sas");

    private readonly IMessageTransport _transport;
    private readonly Direction _send;
    private readonly Direction _receive;
    private readonly SemaphoreSlim _sending = new(1, 1);
    private int _disposed;

    private SecureChannel(IMessageTransport transport, Direction send, Direction receive, DevicePublicKey peer, DeviceDescription peerDescription, string sas)
    {
        _transport = transport;
        _send = send;
        _receive = receive;
        Peer = peer;
        PeerDescription = peerDescription;
        Sas = sas;
    }

    /// <summary>The device at the other end, proven by the handshake.</summary>
    public DevicePublicKey Peer { get; }
    public DeviceDescription PeerDescription { get; }
    /// <summary>The six digits of this connection; equal on both ends exactly when nobody sits in between.</summary>
    public string Sas { get; }
    /// <summary>True when the host added this device during this handshake (the initiator then remembers <see cref="Peer"/>).</summary>
    public bool WasPaired { get; private init; }

    // ------------------------------------------------------------------ initiator (§3, §3.1)

    /// <summary>
    /// Connects to a host. Throws <see cref="HandshakeException"/> when the host is not the expected one, refuses this
    /// device, or anything about the handshake does not verify; the transport is closed then.
    /// </summary>
    public static Task<SecureChannel> ConnectAsync(IMessageTransport transport, DeviceIdentity self, DeviceDescription description,
        PeerExpectation expected, ConnectOptions? options = null, CancellationToken cancellationToken = default) =>
        ConnectAsync(transport, self, description, expected, options ?? new ConnectOptions(), HandshakeEntropy.System, cancellationToken);

    internal static async Task<SecureChannel> ConnectAsync(IMessageTransport transport, DeviceIdentity self, DeviceDescription description,
        PeerExpectation expected, ConnectOptions options, HandshakeEntropy entropy, CancellationToken cancellationToken)
    {
        try
        {
            if (options.Token.Length > MaxTokenLength) throw new ArgumentException("The pairing token is too long.", nameof(options));
            using var exchange = entropy.NewExchangeKey();
            var ephemeral = ExportPoint(exchange.ExportParameters(false));
            var nonce = new byte[NonceLength];
            entropy.Fill(nonce);
            var identity = self.PublicKey.Bytes.ToArray();

            // M1: the commitment fixes this side's ephemeral key, nonce and identity without showing them.
            var hello = new byte[3 + HashLength];
            hello[0] = TypeHello;
            hello[1] = Version;
            hello[2] = options.RequestPairing ? FlagPairing : (byte)0;
            Hash(LabelCommit, ephemeral, nonce, identity).CopyTo(hello, 3);
            await transport.SendAsync(hello, cancellationToken).ConfigureAwait(false);

            // M2: the host's ephemeral key, nonce, identity and its signature over all of it and M1.
            var reply = await ReceiveHandshakeAsync(transport, cancellationToken).ConfigureAwait(false);
            const int replyLength = 2 + PointLength + NonceLength + PointLength + SignatureLength;
            if (reply.Length != replyLength || reply[0] != TypeReply) throw Protocol("The host's reply is malformed.");
            if (reply[1] != Version) throw Protocol($"The host speaks version {reply[1]} of the secure channel, this device version {Version}.");
            var peerEphemeral = reply.AsSpan(2, PointLength).ToArray();
            var peerNonce = reply.AsSpan(2 + PointLength, NonceLength).ToArray();
            var peerIdentity = ParseKey(reply.AsSpan(2 + PointLength + NonceLength, PointLength));
            var peerSignature = reply.AsSpan(replyLength - SignatureLength);
            if (!peerIdentity.Verify(Hash(LabelAuthResponder, hello, peerEphemeral, peerNonce, peerIdentity.Bytes.ToArray()), peerSignature))
            {
                throw new HandshakeException(HandshakeFailure.Authentication, "The host's signature does not verify.");
            }
            // Before anything of this device is shown: the host must be the expected one.
            if (!expected.Matches(peerIdentity))
            {
                throw new HandshakeException(HandshakeFailure.WrongPeer, "This is not the expected host: its key is a different one.");
            }

            var transcript = Hash(LabelTranscript, hello, reply, ephemeral, nonce);
            var (initiatorKey, responderKey) = DeriveKeys(exchange, peerEphemeral, transcript);
            var send = new Direction(initiatorKey);
            var receive = new Direction(responderKey);

            // M3: the reveal in the clear, then this device's identity and signature under the new key.
            var name = NameBytes(description.Name);
            var proof = new byte[PointLength + SignatureLength + 2 + name.Length + 1 + options.Token.Length];
            identity.CopyTo(proof, 0);
            self.Sign(Hash(LabelAuthInitiator, transcript, identity)).CopyTo(proof, PointLength);
            var at = PointLength + SignatureLength;
            proof[at++] = (byte)description.Kind;
            proof[at++] = (byte)name.Length;
            name.CopyTo(proof, at);
            at += name.Length;
            proof[at++] = (byte)options.Token.Length;
            options.Token.Span.CopyTo(proof.AsSpan(at));
            var sealedProof = send.Seal(proof, transcript);
            var reveal = new byte[1 + PointLength + NonceLength + sealedProof.Length];
            reveal[0] = TypeReveal;
            ephemeral.CopyTo(reveal, 1);
            nonce.CopyTo(reveal, 1 + PointLength);
            sealedProof.CopyTo(reveal, 1 + PointLength + NonceLength);
            await transport.SendAsync(reveal, cancellationToken).ConfigureAwait(false);

            // M4, and M5 when a person at the host has to allow this device first.
            var sas = ComputeSas(transcript, identity);
            var (status, peerDescription) = await ReadResultAsync(transport, receive, cancellationToken).ConfigureAwait(false);
            var waited = false;
            if (status == HandshakeStatus.Pending)
            {
                waited = true;
                options.OnPending?.Invoke(sas);
                (status, peerDescription) = await ReadResultAsync(transport, receive, cancellationToken).ConfigureAwait(false);
            }
            if (status != HandshakeStatus.Accepted)
            {
                throw new HandshakeException(HandshakeFailure.Rejected, RejectionText(status), status);
            }
            return new SecureChannel(transport, send, receive, peerIdentity, peerDescription, sas)
            {
                WasPaired = waited || (options.RequestPairing && options.Token.Length > 0),
            };
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------------ responder (§3, §3.1)

    /// <summary>
    /// Answers a device that connected to this host. <paramref name="policy"/> decides, once the device has proven its
    /// key, whether it may come in. Throws <see cref="HandshakeException"/> when it may not or the handshake does not
    /// verify; the transport is closed then.
    /// </summary>
    public static Task<SecureChannel> AcceptAsync(IMessageTransport transport, DeviceIdentity self, DeviceDescription description,
        IAcceptPolicy policy, CancellationToken cancellationToken = default) =>
        AcceptAsync(transport, self, description, policy, HandshakeEntropy.System, cancellationToken);

    internal static async Task<SecureChannel> AcceptAsync(IMessageTransport transport, DeviceIdentity self, DeviceDescription description,
        IAcceptPolicy policy, HandshakeEntropy entropy, CancellationToken cancellationToken)
    {
        try
        {
            // M1.
            var hello = await ReceiveHandshakeAsync(transport, cancellationToken).ConfigureAwait(false);
            if (hello.Length != 3 + HashLength || hello[0] != TypeHello) throw Protocol("The first message is malformed.");
            if (hello[1] != Version) throw Protocol($"The device speaks version {hello[1]} of the secure channel, this host version {Version}.");
            if ((hello[2] & ~FlagPairing) != 0) throw Protocol("Unknown flags.");
            var requestsPairing = (hello[2] & FlagPairing) != 0;
            var commitment = hello.AsSpan(3, HashLength).ToArray();

            // M2.
            using var exchange = entropy.NewExchangeKey();
            var ephemeral = ExportPoint(exchange.ExportParameters(false));
            var nonce = new byte[NonceLength];
            entropy.Fill(nonce);
            var identity = self.PublicKey.Bytes.ToArray();
            var reply = new byte[2 + PointLength + NonceLength + PointLength + SignatureLength];
            reply[0] = TypeReply;
            reply[1] = Version;
            ephemeral.CopyTo(reply, 2);
            nonce.CopyTo(reply, 2 + PointLength);
            identity.CopyTo(reply, 2 + PointLength + NonceLength);
            self.Sign(Hash(LabelAuthResponder, hello, ephemeral, nonce, identity)).CopyTo(reply, reply.Length - SignatureLength);
            await transport.SendAsync(reply, cancellationToken).ConfigureAwait(false);

            // M3.
            var reveal = await ReceiveHandshakeAsync(transport, cancellationToken).ConfigureAwait(false);
            const int clear = 1 + PointLength + NonceLength;
            if (reveal.Length < clear + PointLength + SignatureLength + 3 + TagLength || reveal[0] != TypeReveal) throw Protocol("The third message is malformed.");
            var peerEphemeral = reveal.AsSpan(1, PointLength).ToArray();
            var peerNonce = reveal.AsSpan(1 + PointLength, NonceLength).ToArray();
            var transcript = Hash(LabelTranscript, hello, reply, peerEphemeral, peerNonce);
            var (initiatorKey, responderKey) = DeriveKeys(exchange, peerEphemeral, transcript);
            var receive = new Direction(initiatorKey);
            var send = new Direction(responderKey);
            var proof = receive.Open(reveal.AsSpan(clear), transcript);

            var peerIdentityBytes = proof.AsSpan(0, PointLength).ToArray();
            var peerIdentity = ParseKey(peerIdentityBytes);
            // The values now revealed are the ones committed to in the first message.
            if (!CryptographicOperations.FixedTimeEquals(commitment, Hash(LabelCommit, peerEphemeral, peerNonce, peerIdentityBytes)))
            {
                throw new HandshakeException(HandshakeFailure.Authentication, "The device revealed other values than it committed to.");
            }
            if (!peerIdentity.Verify(Hash(LabelAuthInitiator, transcript, peerIdentityBytes), proof.AsSpan(PointLength, SignatureLength)))
            {
                throw new HandshakeException(HandshakeFailure.Authentication, "The device's signature does not verify.");
            }
            var (peerDescription, token) = ParseDescription(proof.AsSpan(PointLength + SignatureLength), withToken: true);

            // The device is who it says. Whether it may come in is the host's decision.
            var sas = ComputeSas(transcript, peerIdentityBytes);
            var decision = policy.Decide(new PeerOffer(peerIdentity, peerDescription, requestsPairing, token, sas));
            var status = decision.Status;
            await SendResultAsync(transport, send, status, description, cancellationToken).ConfigureAwait(false);
            if (decision.Approval is { } approval)
            {
                bool allowed;
                try { allowed = await approval.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { allowed = false; }
                status = allowed ? HandshakeStatus.Accepted : HandshakeStatus.PairingRefused;
                await SendResultAsync(transport, send, status, description, CancellationToken.None).ConfigureAwait(false);
            }
            if (status != HandshakeStatus.Accepted)
            {
                throw new HandshakeException(HandshakeFailure.Rejected, RejectionText(status), status);
            }
            return new SecureChannel(transport, send, receive, peerIdentity, peerDescription, sas) { WasPaired = decision.Approval is not null };
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ------------------------------------------------------------------ records (§4)

    /// <summary>
    /// Sends one message. Safe to call from several threads: messages go out one after another. Throws
    /// <see cref="IOException"/> when the channel is closed.
    /// </summary>
    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        if (message.Length > MaxMessageLength) throw new ArgumentException($"A message is at most {MaxMessageLength} bytes.", nameof(message));
        if (Volatile.Read(ref _disposed) != 0) throw new IOException("The channel is closed.");
        await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Sealed and sent under the same lock: the counter of a record is its place on the wire.
            await _transport.SendAsync(_send.Seal(message.Span, default), cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Closed while this message waited its turn.
            throw new IOException("The channel is closed.");
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>
    /// The next message, or null when the other side closed. One receiver at a time. A record that does not decrypt
    /// ends the channel with <see cref="HandshakeException"/> (<see cref="HandshakeFailure.Authentication"/>): bytes
    /// were changed, dropped or replayed on the way.
    /// </summary>
    public async ValueTask<byte[]?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        var record = await _transport.ReceiveAsync(MaxMessageLength + TagLength, cancellationToken).ConfigureAwait(false);
        if (record is null) return null;
        try
        {
            return _receive.Open(record, default);
        }
        catch (ObjectDisposedException)
        {
            return null; // closed from this side while a record was on its way in
        }
        catch (HandshakeException)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _transport.DisposeAsync().ConfigureAwait(false);
        _send.Dispose();
        _receive.Dispose();
    }

    // ------------------------------------------------------------------ pieces

    /// <summary>One direction of the channel: its key and the number of the next record.</summary>
    private sealed class Direction(byte[] key) : IDisposable
    {
        private readonly AesGcm _cipher = new(key, TagLength);
        private ulong _counter;

        public byte[] Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associated)
        {
            var record = new byte[plaintext.Length + TagLength];
            Span<byte> nonce = stackalloc byte[12];
            lock (_cipher)
            {
                BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _counter++);
                _cipher.Encrypt(nonce, plaintext, record.AsSpan(0, plaintext.Length), record.AsSpan(plaintext.Length), associated);
            }
            return record;
        }

        public byte[] Open(ReadOnlySpan<byte> record, ReadOnlySpan<byte> associated)
        {
            if (record.Length < TagLength) throw new HandshakeException(HandshakeFailure.Authentication, "A record is too short.");
            var plaintext = new byte[record.Length - TagLength];
            Span<byte> nonce = stackalloc byte[12];
            lock (_cipher)
            {
                BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], _counter);
                try
                {
                    _cipher.Decrypt(nonce, record[..plaintext.Length], record[plaintext.Length..], plaintext, associated);
                }
                catch (CryptographicException)
                {
                    throw new HandshakeException(HandshakeFailure.Authentication, "A record does not decrypt: it was changed, dropped or repeated on the way.");
                }
                _counter++;
            }
            return plaintext;
        }

        public void Dispose() => _cipher.Dispose();
    }

    private static (byte[] Initiator, byte[] Responder) DeriveKeys(ECDiffieHellman own, byte[] peerEphemeral, byte[] transcript)
    {
        byte[] shared;
        try { shared = Agree(own, peerEphemeral); }
        catch (CryptographicException) { throw Protocol("The other side's ephemeral key is not a point of the curve."); }
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, shared, transcript);
        return (HKDF.Expand(HashAlgorithmName.SHA256, prk, KeyLength, LabelInitiatorKey),
                HKDF.Expand(HashAlgorithmName.SHA256, prk, KeyLength, LabelResponderKey));
    }

    /// <summary>The six digits of a connection (§3.2).</summary>
    internal static string ComputeSas(byte[] transcript, byte[] initiatorIdentity) =>
        (BinaryPrimitives.ReadUInt32BigEndian(Hash(LabelSas, transcript, initiatorIdentity)) % 1_000_000).ToString("D6");

    private static async Task SendResultAsync(IMessageTransport transport, Direction send, HandshakeStatus status, DeviceDescription description, CancellationToken cancellationToken)
    {
        var name = NameBytes(description.Name);
        var result = new byte[4 + name.Length];
        result[0] = TypeResult;
        result[1] = (byte)status;
        result[2] = (byte)description.Kind;
        result[3] = (byte)name.Length;
        name.CopyTo(result, 4);
        await transport.SendAsync(send.Seal(result, default), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(HandshakeStatus Status, DeviceDescription Description)> ReadResultAsync(IMessageTransport transport, Direction receive, CancellationToken cancellationToken)
    {
        var record = await ReceiveHandshakeAsync(transport, cancellationToken).ConfigureAwait(false);
        var result = receive.Open(record, default);
        if (result.Length < 4 || result[0] != TypeResult || result[1] > (byte)HandshakeStatus.PairingClosed) throw Protocol("The host's answer is malformed.");
        var (description, _) = ParseDescription(result.AsSpan(2), withToken: false);
        return ((HandshakeStatus)result[1], description);
    }

    // kind(1) ‖ nameLen(1) ‖ name [‖ tokenLen(1) ‖ token]
    private static (DeviceDescription Description, byte[] Token) ParseDescription(ReadOnlySpan<byte> data, bool withToken)
    {
        if (data.Length < 2) throw Protocol("A device description is cut short.");
        var kind = data[0];
        if (kind is not ((byte)DeviceKind.Desktop or (byte)DeviceKind.Phone)) throw Protocol("Unknown device kind.");
        int nameLength = data[1];
        if (nameLength > DeviceDescription.MaxNameBytes || data.Length < 2 + nameLength) throw Protocol("A device name is too long or cut short.");
        string name;
        try { name = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data.Slice(2, nameLength)); }
        catch (ArgumentException) { throw Protocol("A device name is not valid UTF-8."); }
        var rest = data[(2 + nameLength)..];
        if (!withToken) return rest.Length == 0 ? (new DeviceDescription(name, (DeviceKind)kind), []) : throw Protocol("Unexpected bytes after a device description.");
        if (rest.Length < 1 || rest[0] > MaxTokenLength || rest.Length != 1 + rest[0]) throw Protocol("A pairing token is too long or cut short.");
        return (new DeviceDescription(name, (DeviceKind)kind), rest[1..].ToArray());
    }

    /// <summary>The name as it goes on the wire: UTF-8, cut at a character boundary to at most 64 bytes.</summary>
    private static byte[] NameBytes(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length <= DeviceDescription.MaxNameBytes) return bytes;
        var length = DeviceDescription.MaxNameBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80) length--;
        return bytes[..length];
    }

    private static DevicePublicKey ParseKey(ReadOnlySpan<byte> point)
    {
        try { return DevicePublicKey.Parse(point); }
        catch (CryptographicException) { throw Protocol("A device key is not a point of the curve."); }
    }

    private static async Task<byte[]> ReceiveHandshakeAsync(IMessageTransport transport, CancellationToken cancellationToken)
    {
        byte[]? message;
        try { message = await transport.ReceiveAsync(MaxHandshakeLength, cancellationToken).ConfigureAwait(false); }
        catch (IOException e) { throw new HandshakeException(HandshakeFailure.Protocol, e.Message); }
        return message ?? throw new HandshakeException(HandshakeFailure.Closed, "The connection ended during the handshake.");
    }

    private static HandshakeException Protocol(string message) => new(HandshakeFailure.Protocol, message);

    private static string RejectionText(HandshakeStatus status) => status switch
    {
        HandshakeStatus.UnknownDevice => "The host does not know this device.",
        HandshakeStatus.PairingRefused => "The host did not allow this device.",
        HandshakeStatus.PairingClosed => "The host is not accepting new devices right now.",
        _ => "The host refused the connection.",
    };
}
