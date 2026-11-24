using System.Security.Cryptography;

namespace Octopus.Remote.Security;

/// <summary>The responder's answer at the end of the handshake (design/remote/安全通道.md §3).</summary>
public enum HandshakeStatus : byte
{
    Accepted = 0,
    /// <summary>A person at the host has to compare the number and allow this device.</summary>
    Pending = 1,
    /// <summary>The host does not know this device, and it did not ask to be paired.</summary>
    UnknownDevice = 2,
    /// <summary>The person at the host refused, or did not answer in time.</summary>
    PairingRefused = 3,
    /// <summary>The host is not accepting new devices right now (for a device that came with a password: password access is off).</summary>
    PairingClosed = 4,
    /// <summary>The device came with an access password, and it is not this host's.</summary>
    WrongPassword = 5,
    /// <summary>Too many wrong passwords: the host does not try this device's for a while.</summary>
    PasswordLocked = 6,
}

/// <summary>Why a secure channel could not be established.</summary>
public enum HandshakeFailure
{
    /// <summary>A message was malformed, out of order, or of an unknown version.</summary>
    Protocol,
    /// <summary>A signature, the commitment or a record did not verify: the other side is not who it claims, or something changed the bytes on the way.</summary>
    Authentication,
    /// <summary>The host is not the one that was expected (another key than the one remembered or named by the pairing link).</summary>
    WrongPeer,
    /// <summary>The host answered with a refusal; see <see cref="HandshakeException.Status"/>.</summary>
    Rejected,
    /// <summary>The connection ended before the handshake did.</summary>
    Closed,
}

public sealed class HandshakeException(HandshakeFailure failure, string message, HandshakeStatus? status = null) : Exception(message)
{
    public HandshakeFailure Failure { get; } = failure;
    /// <summary>The host's refusal, when <see cref="Failure"/> is <see cref="HandshakeFailure.Rejected"/>.</summary>
    public HandshakeStatus? Status { get; } = status;
}

/// <summary>Which host the initiator is willing to talk to.</summary>
public sealed class PeerExpectation
{
    private readonly DevicePublicKey? _key;
    private readonly byte[]? _fingerprint;
    private readonly string? _deviceId;

    private PeerExpectation(DevicePublicKey? key, byte[]? fingerprint, string? deviceId = null) => (_key, _fingerprint, _deviceId) = (key, fingerprint, deviceId);

    /// <summary>The host this device is paired with.</summary>
    public static PeerExpectation Paired(DevicePublicKey key) => new(key, null);

    /// <summary>The host a pairing link names by the SHA-256 of its key.</summary>
    public static PeerExpectation WithFingerprint(ReadOnlySpan<byte> fingerprint) => new(null, fingerprint.ToArray());

    /// <summary>
    /// The host an account's device list names by its device id. The id comes from the relay, which is not trusted with
    /// the host's identity: this only keeps a device from talking to the wrong host by mistake. What proves the host is
    /// its proof of the access password (design/remote/安全通道.md §3.3), so this is only for password access.
    /// </summary>
    public static PeerExpectation WithDeviceId(string deviceId) => new(null, null, deviceId);

    /// <summary>Any host: only for pairing by comparing numbers, where people check that both sides show the same one.</summary>
    public static PeerExpectation AnyForPairing { get; } = new(null, null);

    internal bool Matches(DevicePublicKey peer) =>
        _key is not null ? _key.Equals(peer)
        : _deviceId is not null ? string.Equals(_deviceId, peer.DeviceId, StringComparison.Ordinal)
        : _fingerprint is null || CryptographicOperations.FixedTimeEquals(_fingerprint, peer.Fingerprint());
}

/// <summary>What the initiator brings to a handshake besides its identity.</summary>
public sealed record ConnectOptions
{
    /// <summary>Ask the host to add this device to its paired devices.</summary>
    public bool RequestPairing { get; init; }
    /// <summary>The one-time token of a pairing link; empty when there is none.</summary>
    public ReadOnlyMemory<byte> Token { get; init; }
    /// <summary>Called with the six digits when the host waits for a person to compare them and allow this device.</summary>
    public Action<string>? OnPending { get; init; }
    /// <summary>
    /// The host's access password as the person typed it: the device asks to be let in with it (and to be remembered),
    /// and only takes a host that proves it knows the same password. Takes the place of <see cref="Token"/>.
    /// </summary>
    public string? Password { get; init; }
}

/// <summary>A device at the host's door: who it says it is, proven to hold that key.</summary>
/// <param name="Sas">The six digits both sides show when a person has to allow it.</param>
public sealed record PeerOffer(DevicePublicKey Key, DeviceDescription Description, bool RequestsPairing, ReadOnlyMemory<byte> Token, string Sas)
{
    /// <summary>The device asks to be let in with the host's access password; <see cref="ProvesPassword"/> checks it.</summary>
    public bool RequestsPasswordAccess { get; init; }
    internal byte[] Transcript { get; init; } = [];

    /// <summary>True when the device proved, for this very handshake, that it holds the key of the access password.</summary>
    public bool ProvesPassword(ReadOnlySpan<byte> passwordKey) =>
        RequestsPasswordAccess && Token.Length == CryptoPrimitives.HashLength
        && CryptographicOperations.FixedTimeEquals(Token.Span, SecureChannel.PasswordProof(passwordKey, initiator: true, Transcript, Key));
}

/// <summary>The host's decision about a <see cref="PeerOffer"/>.</summary>
public sealed class AcceptDecision
{
    private AcceptDecision(HandshakeStatus status, Task<bool>? approval, byte[]? passwordKey = null) => (Status, Approval, PasswordKey) = (status, approval, passwordKey);

    internal HandshakeStatus Status { get; }
    internal Task<bool>? Approval { get; }
    /// <summary>The key of the access password the device proved; the host proves it back in its answer.</summary>
    internal byte[]? PasswordKey { get; }

    public static AcceptDecision Accept { get; } = new(HandshakeStatus.Accepted, null);
    public static AcceptDecision Reject(HandshakeStatus reason) =>
        reason is HandshakeStatus.Accepted or HandshakeStatus.Pending ? throw new ArgumentOutOfRangeException(nameof(reason)) : new(reason, null);
    /// <summary>A person decides: the device is told to wait, then accepted when <paramref name="approval"/> yields true.</summary>
    public static AcceptDecision AskPerson(Task<bool> approval) => new(HandshakeStatus.Pending, approval);
    /// <summary>The device proved the access password (<see cref="PeerOffer.ProvesPassword"/>): accepted, and the host proves it too.</summary>
    public static AcceptDecision AcceptWithPassword(byte[] passwordKey) => new(HandshakeStatus.Accepted, null, passwordKey);
}

/// <summary>Who may connect to this host. Called once per handshake, after the device proved its key.</summary>
public interface IAcceptPolicy
{
    AcceptDecision Decide(PeerOffer offer);
}

/// <summary>Where the handshake's random values come from; replaced in tests to make it repeatable.</summary>
internal sealed class HandshakeEntropy
{
    public static HandshakeEntropy System { get; } = new();
    public Func<ECDiffieHellman> NewExchangeKey { get; init; } = static () => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    public Action<Span<byte>> Fill { get; init; } = RandomNumberGenerator.Fill;
}
