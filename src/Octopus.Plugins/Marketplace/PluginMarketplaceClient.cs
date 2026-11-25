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

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MarketplaceCatalogPage))]
[JsonSerializable(typeof(MarketplaceCatalogEntry))]
[JsonSerializable(typeof(MarketplacePluginSummary))]
[JsonSerializable(typeof(MarketplacePluginVersion))]
internal sealed partial class MarketplaceClientJson : JsonSerializerContext;

/// <summary>HTTP client for the Octopus Server Plugin Marketplace (NativeAOT-safe source-generated JSON).</summary>
public sealed class PluginMarketplaceClient(HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<MarketplaceCatalogPage> SearchAsync(
        string serverBaseUrl,
        string? query = null,
        string? category = null,
        string? sort = null,
        int? apiVersion = null,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = serverBaseUrl.Trim().TrimEnd('/');
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(query)) parts.Add($"q={Uri.EscapeDataString(query.Trim())}");
        if (!string.IsNullOrWhiteSpace(category) && category != "all") parts.Add($"category={Uri.EscapeDataString(category)}");
        if (!string.IsNullOrWhiteSpace(sort)) parts.Add($"sort={Uri.EscapeDataString(sort)}");
        if (apiVersion is { } api) parts.Add($"apiVersion={api}");
        var url = $"{baseUrl}/v1/plugins/catalog" + (parts.Count > 0 ? "?" + string.Join("&", parts) : "");

        using var resp = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, MarketplaceClientJson.Default.MarketplaceCatalogPage, cancellationToken).ConfigureAwait(false)
            ?? new MarketplaceCatalogPage(0, 1, 24, []);
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
        var downloadUrl = Uri.TryCreate(version.DownloadUrl, UriKind.Absolute, out _)
            ? version.DownloadUrl
            : $"{serverBaseUrl.Trim().TrimEnd('/')}/plugins/download/{Uri.EscapeDataString(version.PluginId)}/{Uri.EscapeDataString(version.Version)}.octoplugin";

        var tempFile = Path.Combine(Path.GetTempPath(), $"octopus-dl-{Guid.NewGuid():N}.octoplugin");
        try
        {
            using var resp = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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
            if (!string.IsNullOrWhiteSpace(version.Sha256)
                && !string.Equals(actualSha, version.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Package checksum mismatch: expected {version.Sha256}, got {actualSha}.");
            }

            return tempFile;
        }
        catch
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            throw;
        }
    }
}
