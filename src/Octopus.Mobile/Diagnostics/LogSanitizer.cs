using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Octopus.Mobile.Diagnostics;

/// <summary>
/// Mandatory privacy redaction pipeline applied before logs touch disk and before diagnostic bundles are exported.
/// Uses compile-time <see cref="GeneratedRegexAttribute"/> patterns so it stays 100% NativeAOT-compatible.
/// </summary>
public static partial class LogSanitizer
{
    private const string Redacted = "[REDACTED]";

    // Authorization: Bearer <token> or standalone "Bearer <token>"
    [GeneratedRegex(@"(?i)\b(Bearer\s+)[A-Za-z0-9\-._~+/=]{6,}", RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    // Key/value secrets in query strings, JSON, headers or log lines.
    [GeneratedRegex(@"(?i)([""']?(?:access[_-]?token|refresh[_-]?token|id[_-]?token|token|api[_-]?key|password|passwd|secret|private[_-]?key|authorization|cookie|set-cookie)[""']?\s*[:=]\s*[""']?)(?!Bearer\b|\[REDACTED\])([^\s""',;&\r\n\}]{3,})", RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyValueRegex();

    // Sensitive octopus:// deep-link query payloads (pairing secrets, relay tokens, bot import payloads).
    [GeneratedRegex(@"(?i)\boctopus://(pair|relay|bot(?:/[a-z0-9_-]+)?)\?[^\s""'<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex OctopusDeepLinkRegex();

    // Query parameters carrying tokens/secrets in HTTP(S) URLs.
    [GeneratedRegex(@"(?i)([?&](?:token|access_token|refresh_token|secret|key|code|password|sig|signature)=)[^&\s""'<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex UrlSecretQueryRegex();

    // Email addresses (keep domain for diagnostics while masking local-part).
    [GeneratedRegex(@"\b([A-Za-z0-9._%+-])([A-Za-z0-9._%+-]*)(@[A-Za-z0-9.-]+\.[A-Za-z]{2,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    // Unix / macOS home paths: /Users/<name>/... or /home/<name>/...
    [GeneratedRegex(@"(/(?:Users|home)/)[^/\s""':;,\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnixUserPathRegex();

    // Windows home paths: C:\Users\<name>\...
    [GeneratedRegex(@"(?i)([A-Z]:\\Users\\)[^\\\s""':;,\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsUserPathRegex();

    // IPv4 addresses (masked only when maskIdentifiers is enabled, keeping first two octets for LAN/relay triage).
    [GeneratedRegex(@"\b(\d{1,3}\.\d{1,3})\.\d{1,3}\.\d{1,3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Regex();

    /// <summary>Redacts secrets, deep-link queries, emails, and user home directories from <paramref name="input"/>.</summary>
    public static string Sanitize(string? input, bool maskIdentifiers = false)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var text = input;
        text = BearerTokenRegex().Replace(text, "$1" + Redacted);
        text = SecretKeyValueRegex().Replace(text, "$1" + Redacted);
        text = OctopusDeepLinkRegex().Replace(text, "octopus://$1?" + Redacted);
        text = UrlSecretQueryRegex().Replace(text, "$1" + Redacted);
        text = EmailRegex().Replace(text, "$1***$3");
        text = UnixUserPathRegex().Replace(text, "$1<user>");
        text = WindowsUserPathRegex().Replace(text, "$1<user>");

        if (maskIdentifiers)
        {
            text = Ipv4Regex().Replace(text, "$1.*.*");
        }

        return text;
    }

    /// <summary>
    /// Replaces a potentially identifying name (computer name, project name, server host) with a stable short
    /// pseudonym such as <c>host#7F3A</c> when <paramref name="mask"/> is true, so developers can still correlate
    /// events without seeing private names.
    /// </summary>
    public static string MaskIdentifier(string? value, string prefix, bool mask)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var clean = Sanitize(value.Trim(), maskIdentifiers: mask);
        if (!mask) return clean;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return $"{prefix}#{hash[0]:X2}{hash[1]:X2}";
    }
}
