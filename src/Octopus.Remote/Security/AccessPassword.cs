using System.Security.Cryptography;
using System.Text;

namespace Octopus.Remote.Security;

/// <summary>
/// A host's access password (design/16-账号设备与访问密码.md): eight letters and digits that let a device of the same
/// account in without a person at the host. It never goes over the wire: both ends turn it into a key with a slow
/// derivation salted by the host's key, and prove to each other that they hold that key, bound to the handshake
/// (design/remote/安全通道.md §3.3).
/// </summary>
public static class AccessPassword
{
    public const int Length = 8;
    /// <summary>The characters of a password this app makes: upper-case letters and digits without 0, O, 1, I and L, which people mix up.</summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    /// <summary>PBKDF2-SHA256 rounds: what an attacker pays per guess when trying passwords against a recorded proof.</summary>
    public const int Iterations = 600_000;

    private static readonly byte[] LabelSalt = Encoding.ASCII.GetBytes("octopus-remote/1 access password");

    /// <summary>A new random password, already in its normalized form.</summary>
    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, Length);

    /// <summary>
    /// The password as it is compared: spaces and dashes removed, letters upper-cased. Null when what is left is not
    /// exactly <see cref="Length"/> ASCII letters and digits — a password a person chose has to be eight of them too.
    /// </summary>
    public static string? Normalize(string? typed)
    {
        if (typed is null) return null;
        var builder = new StringBuilder(Length);
        foreach (var c in typed)
        {
            if (c is ' ' or '-' or ' ' or '\t') continue;
            if (!char.IsAsciiLetterOrDigit(c)) return null;
            builder.Append(char.ToUpperInvariant(c));
        }
        return builder.Length == Length ? builder.ToString() : null;
    }

    /// <summary>How a password is shown: <c>ABCD-EFGH</c>.</summary>
    public static string Format(string normalized) =>
        normalized.Length == Length ? $"{normalized[..4]}-{normalized[4..]}" : normalized;

    /// <summary>
    /// The key both ends prove they hold: PBKDF2-SHA256 of the normalized password, salted by a label and the host's
    /// public key, so that one derivation fits one host only. Slow on purpose (<see cref="Iterations"/>).
    /// </summary>
    public static byte[] DeriveKey(string normalized, DevicePublicKey host)
    {
        if (Normalize(normalized) != normalized) throw new ArgumentException("Not a normalized access password.", nameof(normalized));
        var salt = new byte[LabelSalt.Length + DevicePublicKey.Length];
        LabelSalt.CopyTo(salt, 0);
        host.Bytes.CopyTo(salt.AsSpan(LabelSalt.Length));
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.ASCII.GetBytes(normalized), salt, Iterations, HashAlgorithmName.SHA256, CryptoPrimitives.KeyLength);
    }
}
