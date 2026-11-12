using System.Security.Cryptography;
using System.Text;

namespace Octopus.Updates;

/// <summary>
/// Checks <c>update.json.sig</c>: an ECDSA P-256 / SHA-256 signature of the manifest's exact bytes, DER-encoded as
/// <c>openssl dgst -sha256 -sign</c> writes it, then base64 (design/10-自动更新.md §5.2). Any one of the trusted keys may
/// have signed it, so a key can be replaced by shipping the next one first.
/// </summary>
public sealed class ManifestVerifier
{
    private readonly IReadOnlyList<byte[]> _keys;

    /// <param name="publicKeys">SubjectPublicKeyInfo of each trusted key, base64 (the body of a PEM public key).</param>
    public ManifestVerifier(IEnumerable<string> publicKeys)
    {
        _keys = [.. publicKeys.Select(k => Convert.FromBase64String(StripPem(k)))];
        if (_keys.Count == 0) throw new ArgumentException("At least one public key is needed.", nameof(publicKeys));
        foreach (var key in _keys)
        {
            // Fails now, with the key at fault, rather than as "not signed" for every release.
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out _);
        }
    }

    /// <summary>True when one of the trusted keys signed <paramref name="data"/>.</summary>
    /// <param name="signatureFile">The content of the <c>.sig</c> file: base64 text (raw DER is accepted too).</param>
    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signatureFile)
    {
        var signature = DecodeSignature(signatureFile);
        if (signature is null) return false;
        foreach (var key in _keys)
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key, out _);
            if (ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return true;
        }
        return false;
    }

    private static byte[]? DecodeSignature(ReadOnlySpan<byte> file)
    {
        // A DER signature starts with a SEQUENCE tag; base64 text never does.
        if (file.Length > 0 && file[0] == 0x30) return file.ToArray();
        var text = Encoding.ASCII.GetString(file).Trim();
        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string StripPem(string key) =>
        string.Concat(key.Split('\n')
                         .Select(l => l.Trim())
                         .Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal)));
}
