using System.Security.Cryptography;

namespace Octopus.Remote.Security;

/// <summary>What a device is, as it tells its peer during the handshake.</summary>
public enum DeviceKind : byte
{
    Desktop = 1,
    Phone = 2,
}

/// <summary>A device's own description: the name people see, and what kind of device it is.</summary>
public sealed record DeviceDescription(string Name, DeviceKind Kind)
{
    /// <summary>The longest name on the wire, in UTF-8 bytes; a longer one is cut at a character boundary.</summary>
    public const int MaxNameBytes = 64;
}

/// <summary>
/// A device's public key: who it is to its peers. Always the 65-byte uncompressed P-256 point
/// (<c>0x04 ‖ X ‖ Y</c>, design/remote/安全通道.md §1).
/// </summary>
public sealed class DevicePublicKey : IEquatable<DevicePublicKey>
{
    public const int Length = 65;

    private readonly byte[] _point;

    private DevicePublicKey(byte[] point) => _point = point;

    /// <summary>The key from its 65 bytes. Throws <see cref="CryptographicException"/> when they are not a point of the curve.</summary>
    public static DevicePublicKey Parse(ReadOnlySpan<byte> point)
    {
        // Importing checks that the point is on the curve; the key object itself is not kept.
        using var check = CryptoPrimitives.ImportSigningKey(point);
        return new DevicePublicKey(point.ToArray());
    }

    public ReadOnlySpan<byte> Bytes => _point;

    /// <summary>SHA-256 of the key: what a pairing link carries to name the host.</summary>
    public byte[] Fingerprint() => SHA256.HashData(_point);

    /// <summary>The device id: the first 16 bytes of the fingerprint in lower-case base32, 26 characters.</summary>
    public string DeviceId => Base32.Encode(Fingerprint().AsSpan(0, 16));

    /// <summary>True when <paramref name="signature"/> (64 bytes, r‖s) is this device's signature of <paramref name="hash"/>.</summary>
    public bool Verify(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> signature)
    {
        if (signature.Length != CryptoPrimitives.SignatureLength) return false;
        using var key = CryptoPrimitives.ImportSigningKey(_point);
        return key.VerifyHash(hash, signature, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public bool Equals(DevicePublicKey? other) => other is not null && CryptographicOperations.FixedTimeEquals(_point, other._point);
    public override bool Equals(object? obj) => Equals(obj as DevicePublicKey);
    public override int GetHashCode() => BitConverter.ToInt32(_point, 1);
    public override string ToString() => DeviceId;
}

/// <summary>
/// This device's key pair (ECDSA P-256). Made once per installation and kept in the system's keystore; the private
/// part never leaves the device and is never sent.
/// </summary>
public sealed class DeviceIdentity : IDisposable
{
    private readonly ECDsa _key;

    private DeviceIdentity(ECDsa key)
    {
        _key = key;
        PublicKey = DevicePublicKey.Parse(CryptoPrimitives.ExportPoint(key.ExportParameters(false)));
    }

    public static DeviceIdentity Create() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>The identity from what <see cref="Export"/> returned.</summary>
    public static DeviceIdentity Import(ReadOnlySpan<byte> pkcs8)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            if (key.KeySize != 256) throw new CryptographicException("The device key must be a P-256 key.");
            return new DeviceIdentity(key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>The private key (PKCS#8), for the keystore only.</summary>
    public byte[] Export() => _key.ExportPkcs8PrivateKey();

    public DevicePublicKey PublicKey { get; }
    public string Id => PublicKey.DeviceId;

    internal byte[] Sign(ReadOnlySpan<byte> hash) => _key.SignHash(hash, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _key.Dispose();
}
