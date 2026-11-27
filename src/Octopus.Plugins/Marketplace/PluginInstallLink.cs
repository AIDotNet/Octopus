namespace Octopus.Plugins.Marketplace;

/// <summary>Parsed <c>octopus://plugin/install?id=...&amp;version=...&amp;server=...</c> deep link request.</summary>
public sealed record PluginInstallRequest(string PluginId, string? Version, string? ServerUrl);

/// <summary>
/// Parses and builds <c>octopus://plugin/install</c> deep links sent by the Web Plugin Marketplace
/// to open the desktop client's plugin confirmation modal.
/// </summary>
public static class PluginInstallLink
{
    public const string Scheme = "octopus";
    public const string Host = "plugin";
    public const string Path = "/install";

    public static bool IsPluginInstallUri(Uri? uri) =>
        uri is not null
        && string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.AbsolutePath.TrimEnd('/'), Path, StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(Uri? uri, out PluginInstallRequest? request, out string? error)
    {
        request = null;
        error = null;
        if (!IsPluginInstallUri(uri) || uri is null)
        {
            error = "Not an octopus://plugin/install link.";
            return false;
        }

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("id", out var rawId) || string.IsNullOrWhiteSpace(rawId))
        {
            error = "Missing plugin id.";
            return false;
        }

        var id = rawId.Trim();
        if (id.Length > 128 || id.Contains('/') || id.Contains('\\') || id.Contains(".."))
        {
            error = "Invalid plugin id.";
            return false;
        }

        query.TryGetValue("version", out var rawVersion);
        var version = string.IsNullOrWhiteSpace(rawVersion) ? null : rawVersion.Trim();

        string? serverUrl = null;
        if (query.TryGetValue("server", out var rawServer) && !string.IsNullOrWhiteSpace(rawServer))
        {
            if (!Uri.TryCreate(rawServer.Trim(), UriKind.Absolute, out var serverUri)
                || (serverUri.Scheme != Uri.UriSchemeHttp && serverUri.Scheme != Uri.UriSchemeHttps))
            {
                error = "Invalid server URL.";
                return false;
            }
            serverUrl = serverUri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        request = new PluginInstallRequest(id, version, serverUrl);
        return true;
    }

    public static string BuildUri(string pluginId, string? version = null, string? serverBaseUrl = null)
    {
        var parts = new List<string> { $"id={Uri.EscapeDataString(pluginId.Trim())}" };
        if (!string.IsNullOrWhiteSpace(version))
        {
            parts.Add($"version={Uri.EscapeDataString(version.Trim())}");
        }
        if (!string.IsNullOrWhiteSpace(serverBaseUrl))
        {
            parts.Add($"server={Uri.EscapeDataString(serverBaseUrl.Trim().TrimEnd('/'))}");
        }
        return $"{Scheme}://{Host}{Path}?{string.Join("&", parts)}";
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return map;
        var span = query.StartsWith('?') ? query[1..] : query;
        foreach (var pair in span.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = Uri.UnescapeDataString(pair[..eq].Replace('+', ' '));
            var val = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            if (!string.IsNullOrEmpty(key)) map[key] = val;
        }
        return map;
    }
}
