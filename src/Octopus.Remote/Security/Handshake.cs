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
    /// <summary>The host is not accepting new devices right now.</summary>
    PairingClosed = 4,
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

    private PeerExpectation(DevicePublicKey? key, byte[]? fingerprint) => (_key, _fingerprint) = (key, fingerprint);

    /// <summary>The host this device is paired with.</summary>
    public static PeerExpectation Paired(DevicePublicKey key) => new(key, null);

    /// <summary>The host a pairing link names by the SHA-256 of its key.</summary>
    public static PeerExpectation WithFingerprint(ReadOnlySpan<byte> fingerprint) => new(null, fingerprint.ToArray());

    /// <summary>Any host: only for pairing by comparing numbers, where people check that both sides show the same one.</summary>
    public static PeerExpectation AnyForPairing { get; } = new(null, null);

    internal bool Matches(DevicePublicKey peer) =>
        _key is not null ? _key.Equals(peer)
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
}

/// <summary>A device at the host's door: who it says it is, proven to hold that key.</summary>
/// <param name="Sas">The six digits both sides show when a person has to allow it.</param>
public sealed record PeerOffer(DevicePublicKey Key, DeviceDescription Description, bool RequestsPairing, ReadOnlyMemory<byte> Token, string Sas);

/// <summary>The host's decision about a <see cref="PeerOffer"/>.</summary>
public sealed class AcceptDecision
{
    private AcceptDecision(HandshakeStatus status, Task<bool>? approval) => (Status, Approval) = (status, approval);

    internal HandshakeStatus Status { get; }
    internal Task<bool>? Approval { get; }

    public static AcceptDecision Accept { get; } = new(HandshakeStatus.Accepted, null);
    public static AcceptDecision Reject(HandshakeStatus reason) =>
        reason is HandshakeStatus.Accepted or HandshakeStatus.Pending ? throw new ArgumentOutOfRangeException(nameof(reason)) : new(reason, null);
    /// <summary>A person decides: the device is told to wait, then accepted when <paramref name="approval"/> yields true.</summary>
    public static AcceptDecision AskPerson(Task<bool> approval) => new(HandshakeStatus.Pending, approval);
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
