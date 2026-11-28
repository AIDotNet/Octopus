using System.Net;

namespace Octopus.Plugins.Marketplace;

/// <summary>
/// Boundary validation for marketplace endpoints and package integrity metadata:
/// external endpoints must use <c>https://</c>, while <c>http://</c> is deliberately permitted only for
/// local loopback development (<c>localhost</c>, <c>127.0.0.1</c>, <c>[::1]</c>); package downloads require
/// a valid 64-character hex SHA-256 digest before any download begins.
/// </summary>
public static class MarketplaceEndpointPolicy
{
    public static bool IsValidSha256(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256)) return false;
        var span = sha256.AsSpan().Trim();
        if (span.Length != 64) return false;
        foreach (var ch in span)
        {
            if (!char.IsAsciiHexDigit(ch)) return false;
        }
        return true;
    }

    public static bool IsLocalhost(Uri uri)
    {
        var host = TrimBrackets(uri.IdnHost);
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    public static bool TryValidateServerUrl(string? rawUrl, out Uri? uri, out string? error, bool allowLocalhostHttp = true)
    {
        uri = null;
        error = null;
        if (string.IsNullOrWhiteSpace(rawUrl)
            || !Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var parsed)
            || string.IsNullOrWhiteSpace(parsed.Host))
        {
            error = "Invalid server URL.";
            return false;
        }
        if (parsed.UserInfo.Length > 0)
        {
            error = "Server URL must not include credentials.";
            return false;
        }
        if (string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            uri = parsed;
            return true;
        }
        if (string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            if (allowLocalhostHttp && IsLocalhost(parsed))
            {
                uri = parsed;
                return true;
            }
            error = "External marketplace URLs must use https:// (http:// is only allowed for localhost development).";
            return false;
        }
        error = "Server URL must use https://.";
        return false;
    }

    public static Uri ValidateServerUrl(string? rawUrl, bool allowLocalhostHttp = true)
    {
        if (!TryValidateServerUrl(rawUrl, out var uri, out var error, allowLocalhostHttp) || uri is null)
        {
            throw new InvalidOperationException(error ?? "Invalid marketplace server URL.");
        }
        return uri;
    }

    /// <summary>
    /// Resolves and validates a package download URL against its marketplace source.
    /// An external <c>https://</c> marketplace never permits <c>http://</c> downloads (even on loopback);
    /// a localhost development server may serve downloads over <c>http://</c> loopback or <c>https://</c>.
    /// </summary>
    public static bool TryResolveDownloadUrl(
        Uri serverBaseUri,
        string? rawDownloadUrl,
        string pluginId,
        string version,
        out Uri? downloadUri,
        out string? error)
    {
        downloadUri = null;
        error = null;
        var allowLocalhostHttp = IsLocalhost(serverBaseUri);
        var trimmed = rawDownloadUrl?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            var fallback = $"{serverBaseUri.AbsoluteUri.TrimEnd('/')}/plugins/download/{Uri.EscapeDataString(pluginId)}/{Uri.EscapeDataString(version)}.octoplugin";
            return TryValidateServerUrl(fallback, out downloadUri, out error, allowLocalhostHttp);
        }

        if (trimmed.Contains('\\', StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            error = "Invalid package download URL.";
            return false;
        }

        // Check root-relative paths before UriKind.Absolute: on Unix/macOS, leading '/' parses as an absolute file:// URI.
        if (trimmed.StartsWith('/'))
        {
            if (Uri.TryCreate(serverBaseUri, trimmed, out var rootRelative)
                && IsSameOrigin(serverBaseUri, rootRelative))
            {
                return TryValidateServerUrl(rootRelative.AbsoluteUri, out downloadUri, out error, allowLocalhostHttp);
            }

            error = "Invalid package download URL.";
            return false;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
        {
            return TryValidateServerUrl(absolute.AbsoluteUri, out downloadUri, out error, allowLocalhostHttp);
        }

        if (Uri.TryCreate(trimmed, UriKind.Relative, out _))
        {
            var baseDirectory = new Uri(serverBaseUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
            if (Uri.TryCreate(baseDirectory, trimmed, out var relative)
                && IsSameOrigin(serverBaseUri, relative))
            {
                return TryValidateServerUrl(relative.AbsoluteUri, out downloadUri, out error, allowLocalhostHttp);
            }
        }

        error = "Invalid package download URL.";
        return false;
    }

    private static bool IsSameOrigin(Uri expected, Uri candidate) =>
        string.Equals(expected.Scheme, candidate.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.IdnHost, candidate.IdnHost, StringComparison.OrdinalIgnoreCase)
        && expected.Port == candidate.Port;

    public static Uri ResolveDownloadUrl(Uri serverBaseUri, string? rawDownloadUrl, string pluginId, string version)
    {
        if (!TryResolveDownloadUrl(serverBaseUri, rawDownloadUrl, pluginId, version, out var downloadUri, out var error) || downloadUri is null)
        {
            throw new InvalidOperationException(error ?? "Invalid package download URL.");
        }
        return downloadUri;
    }

    public static bool TryValidateExternalUrl(string? rawUrl, out Uri? uri) =>
        TryValidateServerUrl(rawUrl, out uri, out _, allowLocalhostHttp: true);

    private static string TrimBrackets(string host) =>
        host.Length >= 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
}
