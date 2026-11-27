using System.Buffers.Text;
using System.Text;

namespace Octopus.Remote.Security;

/// <summary>
/// What a host hands to a device to pair without comparing numbers: which host, its key's fingerprint, a one-time
/// token and where to reach it (design/remote/安全通道.md §6). Whoever holds a link may pair, so it only travels by
/// screen, camera or clipboard.
/// </summary>
/// <param name="DeviceId">The host's device id.</param>
/// <param name="Fingerprint">SHA-256 of the host's public key.</param>
/// <param name="Token">The one-time token, valid for a few minutes.</param>
/// <param name="Addresses">Where the host listens, most direct first.</param>
public sealed record PairingLink(string DeviceId, byte[] Fingerprint, byte[] Token, string Name, IReadOnlyList<string> Addresses)
{
    public const string Scheme = "octopus";
    private const string Prefix = Scheme + "://pair?";
    private const string PrefixWithSlash = Scheme + "://pair/?";

    public override string ToString()
    {
        var text = new StringBuilder(Prefix)
            .Append("v=1&id=").Append(DeviceId)
            .Append("&fp=").Append(Base64Url.EncodeToString(Fingerprint))
            .Append("&t=").Append(Base64Url.EncodeToString(Token))
            .Append("&n=").Append(Uri.EscapeDataString(Name));
        foreach (var address in Addresses) text.Append("&a=").Append(Uri.EscapeDataString(address));
        return text.ToString();
    }

    /// <summary>The link from its text; null when it is not a pairing link of a version this device knows, or is incomplete.</summary>
    public static PairingLink? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Strip surrounding punctuation/whitespace and any embedded line breaks or spaces introduced when sharing
        // the link through chat apps or SMS (values like the computer name are already percent-encoded).
        var cleaned = StripWhitespace(text.Trim().Trim('<', '>', '"', '\''));
        int queryStart;
        if (cleaned.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) queryStart = Prefix.Length;
        else if (cleaned.StartsWith(PrefixWithSlash, StringComparison.OrdinalIgnoreCase)) queryStart = PrefixWithSlash.Length;
        else return null;
        string? version = null, id = null, name = null;
        byte[]? fingerprint = null, token = null;
        var addresses = new List<string>();
        foreach (var pair in cleaned[queryStart..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var cut = pair.IndexOf('=');
            if (cut <= 0) return null;
            string value;
            try { value = Uri.UnescapeDataString(pair[(cut + 1)..]); }
            catch (UriFormatException) { return null; }
            switch (pair[..cut])
            {
                case "v": version = value; break;
                case "id": id = value; break;
                case "n": name = value; break;
                case "a": addresses.Add(value); break;
                case "fp": fingerprint = Decode(value); break;
                case "t": token = Decode(value); break;
            }
        }
        if (version != "1" || string.IsNullOrEmpty(id) || fingerprint is not { Length: 32 } || token is not { Length: > 0 and <= SecureChannel.MaxTokenLength })
        {
            return null;
        }
        return new PairingLink(id, fingerprint, token, name ?? "", addresses);
    }

    private static string StripWhitespace(string value)
    {
        var hasWhitespace = false;
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                hasWhitespace = true;
                break;
            }
        }
        if (!hasWhitespace) return value;
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (!char.IsWhiteSpace(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    private static byte[]? Decode(string value)
    {
        try { return Base64Url.DecodeFromChars(value); }
        catch (FormatException) { return null; }
    }
}
