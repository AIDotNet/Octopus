using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Octopus.Plugins.Marketplace;

public sealed record MarketplacePluginSummary(
    string Id,
    string PublisherSlug,
    string PublisherName,
    bool PublisherVerified,
    string Name,
    string? LocalizedNameZh,
    string Description,
    string? LocalizedDescriptionZh,
    string Category,
    IReadOnlyList<string> Tags,
    string? Icon,
    string? Homepage,
    string? License,
    long Downloads,
    bool Featured,
    string? LatestVersion,
    DateTime UpdatedAt);

public sealed record MarketplacePluginVersion(
    string PluginId,
    string Version,
    int ApiVersion,
    string? OctopusRange,
    IReadOnlyList<string> Permissions,
    string RiskLevel,
    long PackageSize,
    string Sha256,
    string DownloadUrl,
    string ReviewStatus,
    string? Changelog,
    string? SubmissionNotes,
    DateTime SubmittedAt,
    DateTime? PublishedAt);

public sealed record MarketplaceCatalogEntry(MarketplacePluginSummary Plugin, MarketplacePluginVersion Latest);

public sealed record MarketplaceCatalogPage(int Total, int Page, int PageSize, IReadOnlyList<MarketplaceCatalogEntry> Items);

public sealed record MarketplacePluginDetail(
    MarketplacePluginSummary Plugin,
    string? ReadmeMd,
    IReadOnlyList<MarketplacePluginVersion> Versions);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MarketplaceCatalogPage))]
[JsonSerializable(typeof(MarketplaceCatalogEntry))]
[JsonSerializable(typeof(MarketplacePluginSummary))]
[JsonSerializable(typeof(MarketplacePluginVersion))]
[JsonSerializable(typeof(MarketplacePluginDetail))]
internal sealed partial class MarketplaceClientJson : JsonSerializerContext;

/// <summary>HTTP client for the Octopus Server Plugin Marketplace (NativeAOT-safe source-generated JSON).</summary>
public sealed class PluginMarketplaceClient(HttpClient? httpClient = null)
{
    public const int MaxRedirects = 5;

    private readonly HttpClient _http = httpClient ?? new HttpClient(CreateDefaultHandler()) { Timeout = TimeSpan.FromSeconds(20) };

    internal static SocketsHttpHandler CreateDefaultHandler() => new()
    {
        AllowAutoRedirect = false,
    };

    public async Task<MarketplaceCatalogPage> SearchAsync(
        string serverBaseUrl,
        string? query = null,
        string? category = null,
        string? sort = null,
        int? apiVersion = null,
        CancellationToken cancellationToken = default)
    {
        var baseUri = MarketplaceEndpointPolicy.ValidateServerUrl(serverBaseUrl);
        var baseUrl = baseUri.AbsoluteUri.TrimEnd('/');
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(query)) parts.Add($"q={Uri.EscapeDataString(query.Trim())}");
        if (!string.IsNullOrWhiteSpace(category) && category != "all") parts.Add($"category={Uri.EscapeDataString(category)}");
        if (!string.IsNullOrWhiteSpace(sort)) parts.Add($"sort={Uri.EscapeDataString(sort)}");
        if (apiVersion is { } api) parts.Add($"apiVersion={api}");
        var url = $"{baseUrl}/v1/plugins/catalog" + (parts.Count > 0 ? "?" + string.Join("&", parts) : "");

        using var resp = await GetWithValidatedRedirectsAsync(new Uri(url), baseUri, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, MarketplaceClientJson.Default.MarketplaceCatalogPage, cancellationToken).ConfigureAwait(false)
            ?? new MarketplaceCatalogPage(0, 1, 24, []);
    }

    /// <summary>Fetches a single published plugin's metadata and approved versions (<c>/api/plugins/{id}</c>).</summary>
    public async Task<MarketplacePluginDetail?> GetDetailAsync(
        string serverBaseUrl,
        string pluginId,
        CancellationToken cancellationToken = default)
    {
        var baseUri = MarketplaceEndpointPolicy.ValidateServerUrl(serverBaseUrl);
        var baseUrl = baseUri.AbsoluteUri.TrimEnd('/');
        var url = $"{baseUrl}/api/plugins/{Uri.EscapeDataString(pluginId.Trim())}";
        using var resp = await GetWithValidatedRedirectsAsync(new Uri(url), baseUri, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, MarketplaceClientJson.Default.MarketplacePluginDetail, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads a <c>.octoplugin</c> package to a temporary file and verifies its SHA-256 checksum before returning the path.
    /// Caller owns deleting the returned file after staging.
    /// </summary>
    public async Task<string> DownloadVerifiedAsync(
        string serverBaseUrl,
        MarketplacePluginVersion version,
        CancellationToken cancellationToken = default)
    {
        if (!MarketplaceEndpointPolicy.IsValidSha256(version.Sha256))
        {
            throw new InvalidDataException("Package metadata is missing a valid SHA-256 checksum.");
        }

        var expectedSha = version.Sha256.Trim();
        var baseUri = MarketplaceEndpointPolicy.ValidateServerUrl(serverBaseUrl);
        var downloadUri = MarketplaceEndpointPolicy.ResolveDownloadUrl(baseUri, version.DownloadUrl, version.PluginId, version.Version);

        var tempFile = Path.Combine(Path.GetTempPath(), $"octopus-dl-{Guid.NewGuid():N}.octoplugin");
        try
        {
            using var resp = await GetWithValidatedRedirectsAsync(downloadUri, baseUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var input = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];

            await using (var output = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
            {
                while (true)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                }
            }

            var actualSha = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Package checksum mismatch: expected {expectedSha}, got {actualSha}.");
            }

            return tempFile;
        }
        catch
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            throw;
        }
    }

    private async Task<HttpResponseMessage> GetWithValidatedRedirectsAsync(
        Uri initialUri,
        Uri sourceBaseUri,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        var allowLocalhostHttp = MarketplaceEndpointPolicy.IsLocalhost(sourceBaseUri);
        var currentUri = MarketplaceEndpointPolicy.ValidateServerUrl(initialUri.AbsoluteUri, allowLocalhostHttp);

        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            var response = await _http.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
            ValidateResponseUri(response, sourceBaseUri, allowLocalhostHttp);

            if (!IsRedirectStatus(response.StatusCode) || response.Headers.Location is not { } location)
            {
                return response;
            }

            response.Dispose();
            if (hop >= MaxRedirects)
            {
                throw new InvalidOperationException("Too many redirects.");
            }

            if (!TryResolveRedirectUri(currentUri, location, out var nextUri) || nextUri is null)
            {
                throw new InvalidOperationException("Invalid redirect URL.");
            }

            // Once a hop leaves loopback over HTTPS, never allow a subsequent hop to pivot back to plaintext loopback.
            allowLocalhostHttp = allowLocalhostHttp && MarketplaceEndpointPolicy.IsLocalhost(currentUri);
            currentUri = MarketplaceEndpointPolicy.ValidateServerUrl(nextUri.AbsoluteUri, allowLocalhostHttp);
        }
    }

    private static bool IsRedirectStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool TryResolveRedirectUri(Uri currentUri, Uri location, out Uri? nextUri)
    {
        nextUri = null;
        var raw = location.OriginalString;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        if (!location.IsAbsoluteUri)
        {
            return Uri.TryCreate(currentUri, location, out nextUri);
        }

        if (location.IsFile && raw.StartsWith('/') && !raw.StartsWith("//", StringComparison.Ordinal))
        {
            return Uri.TryCreate(currentUri, raw, out nextUri);
        }

        nextUri = location;
        return true;
    }

    private static void ValidateResponseUri(HttpResponseMessage response, Uri sourceBaseUri, bool allowLocalhostHttp)
    {
        if (response.RequestMessage?.RequestUri is { } finalUri)
        {
            MarketplaceEndpointPolicy.ValidateServerUrl(
                finalUri.AbsoluteUri,
                allowLocalhostHttp: allowLocalhostHttp && MarketplaceEndpointPolicy.IsLocalhost(sourceBaseUri));
        }
    }
}
