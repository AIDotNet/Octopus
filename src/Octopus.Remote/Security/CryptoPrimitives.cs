using System.Security.Cryptography;
using System.Text;

namespace Octopus.Remote.Security;

/// <summary>The few building blocks of the secure channel, all from the base class library (design/remote/安全通道.md §1).</summary>
internal static class CryptoPrimitives
{
    public const int PointLength = 65;
    public const int SignatureLength = 64;
    public const int NonceLength = 32;
    public const int HashLength = 32;
    public const int KeyLength = 32;
    public const int TagLength = 16;

    public static byte[] Label(string text) => Encoding.ASCII.GetBytes(text);

    /// <summary>SHA-256 of the parts, one after another.</summary>
    public static byte[] Hash(params ReadOnlySpan<byte[]> parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts) hash.AppendData(part);
        return hash.GetHashAndReset();
    }

    public static byte[] ExportPoint(ECParameters parameters)
    {
        var point = new byte[PointLength];
        point[0] = 0x04;
        Pad(parameters.Q.X, point.AsSpan(1, 32));
        Pad(parameters.Q.Y, point.AsSpan(33, 32));
        return point;
    }

    public static ECDsa ImportSigningKey(ReadOnlySpan<byte> point) => ECDsa.Create(ParametersOf(point));

    public static ECDiffieHellman ImportExchangeKey(ReadOnlySpan<byte> point) => ECDiffieHellman.Create(ParametersOf(point));

    /// <summary>The input keying material of a key exchange: SHA-256 of the shared point's X coordinate.</summary>
    public static byte[] Agree(ECDiffieHellman own, ReadOnlySpan<byte> peerPoint)
    {
        using var peer = ImportExchangeKey(peerPoint);
        return own.DeriveKeyFromHash(peer.PublicKey, HashAlgorithmName.SHA256);
    }

    private static ECParameters ParametersOf(ReadOnlySpan<byte> point)
    {
        if (point.Length != PointLength || point[0] != 0x04) throw new CryptographicException("Not an uncompressed P-256 point.");
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point.Slice(1, 32).ToArray(), Y = point.Slice(33, 32).ToArray() },
        };
        // Refuses a point that is not on the curve (an attacker's way to learn bits of a private key).
        parameters.Validate();
        return parameters;
    }

    // Coordinates come without leading zero bytes on some platforms: right-align them in 32 bytes.
    private static void Pad(byte[]? value, Span<byte> destination)
    {
        destination.Clear();
        if (value is null || value.Length > destination.Length) throw new CryptographicException("Unexpected coordinate length.");
        value.CopyTo(destination[(destination.Length - value.Length)..]);
    }
}

/// <summary>RFC 4648 base32 in lower case without padding: device ids are typed and read by people.</summary>
public static class Base32
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }
}
