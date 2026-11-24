using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Octopus.Core.Updates;

namespace Octopus.Updates;

/// <summary>Where newer releases are found (design/10-自动更新.md §5.4).</summary>
public interface IUpdateFeed
{
    /// <summary>
    /// The newest release above <paramref name="floor"/> in <paramref name="channel"/> with a validly signed manifest;
    /// null when there is none. Throws <see cref="UpdateException"/> when the feed cannot be read.
    /// </summary>
    Task<UpdateOffer?> FindAsync(AppVersion floor, UpdateChannel channel, string target, CancellationToken cancellationToken);
}

/// <summary>What went wrong, for the message the app shows.</summary>
public enum UpdateErrorKind
{
    /// <summary>No connection, a timeout, a server error.</summary>
    Network,
    /// <summary>GitHub's limit for requests without an account was reached.</summary>
    RateLimited,
    /// <summary>The downloaded file is not what the signed manifest describes.</summary>
    Integrity,
    /// <summary>The package could not be unpacked or its code signature is wrong.</summary>
    Package,
    /// <summary>No space left, no permission for the cache folder.</summary>
    Disk,
    /// <summary>The installer ran and the version did not change.</summary>
    Install,
}

public sealed class UpdateException(UpdateErrorKind kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public UpdateErrorKind Kind { get; } = kind;
}

/// <summary>
/// Reads GitHub's release list, newest first, and returns the first release that is newer, in the channel, and has a
/// manifest signed by a trusted key. Releases without one (a fork, a dry run without the signing key) are skipped.
/// </summary>
public sealed class GitHubReleasesFeed(HttpClient http, ManifestVerifier verifier, string releasesUrl) : IUpdateFeed
{
    public const string DefaultReleasesUrl = "https://api.github.com/repos/AIDotNet/Octopus/releases?per_page=30";
    public const string ManifestName = "update.json";
    public const string SignatureName = "update.json.sig";
    private const int MaxManifestBytes = 1 << 20;
    /// <summary>How many candidates are tried before giving up: each costs two downloads.</summary>
    private const int MaxCandidates = 5;
    /// <summary>How many releases' notes are put together, and how long one release's notes may be.</summary>
    private const int MaxNotesReleases = 10;
    private const int MaxNotesChars = 20_000;

    private string? _etag;
    private List<GitHubRelease>? _cached;

    public async Task<UpdateOffer?> FindAsync(AppVersion floor, UpdateChannel channel, string target, CancellationToken cancellationToken)
    {
        var releases = await ListAsync(cancellationToken).ConfigureAwait(false);
        var candidates = releases
            .Where(r => !r.Draft)
            .Select(r => (Release: r, Version: AppVersion.TryParse(r.TagName, out var v) ? v : null))
            .Where(c => c.Version is not null && c.Version > floor && (channel == UpdateChannel.Preview || !c.Version.IsPreview))
            .OrderByDescending(c => c.Version)
            .ToList();
        foreach (var (release, version) in candidates.Take(MaxCandidates))
        {
            var manifest = await ReadManifestAsync(release, version!, cancellationToken).ConfigureAwait(false);
            if (manifest is not null)
            {
                manifest.NotesUrl ??= release.HtmlUrl;
                // This release and the older ones the person skipped over on the way up.
                var notes = BuildNotes(candidates.Where(c => c.Version! <= version!).Take(MaxNotesReleases).Select(c => (c.Release, c.Version!)).ToList());
                return new UpdateOffer(version!, manifest, manifest.Assets.GetValueOrDefault(target)) { Notes = notes };
            }
        }
        return null;
    }

    /// <summary>The notes of the releases, newest first; a heading per version when there is more than one.</summary>
    private static string? BuildNotes(List<(GitHubRelease Release, AppVersion Version)> releases)
    {
        var parts = new List<string>();
        foreach (var (release, version) in releases)
        {
            var body = release.Body is { } text ? ReleaseNotesTidy.Apply(text) : null;
            if (string.IsNullOrEmpty(body)) continue;
            if (body.Length > MaxNotesChars) body = body[..MaxNotesChars].TrimEnd() + "\n\n…";
            parts.Add(releases.Count > 1 ? $"## {version}\n\n{body}" : body);
        }
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private async Task<List<GitHubRelease>> ListAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, releasesUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (_etag is not null && _cached is not null) request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && _cached is not null) return _cached;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0")
        {
            throw new UpdateException(UpdateErrorKind.RateLimited, "GitHub's request limit was reached.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException(UpdateErrorKind.Network, $"The release list answered {(int)response.StatusCode}.");
        }
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var releases = await JsonSerializer.DeserializeAsync(stream, UpdatesJsonContext.Default.ListGitHubRelease, cancellationToken).ConfigureAwait(false) ?? [];
            _etag = response.Headers.ETag?.ToString();
            _cached = releases;
            return releases;
        }
        catch (JsonException e)
        {
            throw new UpdateException(UpdateErrorKind.Network, "The release list could not be read.", e);
        }
    }

    private async Task<UpdateManifest?> ReadManifestAsync(GitHubRelease release, AppVersion version, CancellationToken cancellationToken)
    {
        var manifestAsset = release.Assets.FirstOrDefault(a => a.Name == ManifestName);
        var signatureAsset = release.Assets.FirstOrDefault(a => a.Name == SignatureName);
        if (manifestAsset is null || signatureAsset is null) return null;
        var data = await DownloadSmallAsync(manifestAsset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);
        var signature = await DownloadSmallAsync(signatureAsset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false);
        if (data is null || signature is null || !verifier.Verify(data, signature)) return null;
        try
        {
            var manifest = JsonSerializer.Deserialize(data, UpdatesJsonContext.Default.UpdateManifest);
            // The signature covers the manifest, not the tag: a validly signed manifest of another version is not this release.
            return manifest is not null && AppVersion.TryParse(manifest.Version, out var stated) && stated == version ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<byte[]?> DownloadSmallAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxManifestBytes) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return bytes.Length > MaxManifestBytes ? null : bytes;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException(UpdateErrorKind.Network, "The request timed out.", e);
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException(UpdateErrorKind.Network, e.Message, e);
        }
    }
}

/// <summary>
/// Checks <c>GET /v1/updates/check</c> on Octopus Server (default <c>https://octoterm.si</c>). The server is treated as
/// untrusted: the returned <c>update.json</c> raw bytes must still pass ECDSA signature verification against
/// <see cref="UpdateEndpoints.PublicKeys"/>, and the package URL, size and SHA-256 are read solely from that signed manifest.
/// </summary>
public sealed class ServerUpdateFeed(HttpClient http, ManifestVerifier verifier, string checkUrl) : IUpdateFeed
{
    private const int MaxManifestBytes = 1 << 20;
    private const int MaxNotesReleases = 10;
    private const int MaxNotesChars = 20_000;

    private string? _lastRequestKey;
    private string? _etag;
    private ServerUpdateCheckResponse? _cached;

    public async Task<UpdateOffer?> FindAsync(AppVersion floor, UpdateChannel channel, string target, CancellationToken cancellationToken)
    {
        var channelValue = channel == UpdateChannel.Preview ? UpdateSettingValues.Preview : UpdateSettingValues.Stable;
        var separator = checkUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var url = $"{checkUrl}{separator}current={Uri.EscapeDataString(floor.ToString())}&channel={channelValue}&target={Uri.EscapeDataString(target)}";
        var key = $"{floor}|{channelValue}|{target}";

        var answer = await QueryAsync(url, key, cancellationToken).ConfigureAwait(false);
        if (!answer.UpdateAvailable || string.IsNullOrWhiteSpace(answer.Manifest) || string.IsNullOrWhiteSpace(answer.Signature))
        {
            return null;
        }

        byte[] manifestBytes;
        try
        {
            manifestBytes = Convert.FromBase64String(answer.Manifest.Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        if (manifestBytes.Length == 0 || manifestBytes.Length > MaxManifestBytes)
        {
            return null;
        }

        var signatureBytes = Encoding.ASCII.GetBytes(answer.Signature.Trim());
        if (!verifier.Verify(manifestBytes, signatureBytes))
        {
            return null;
        }

        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(manifestBytes, UpdatesJsonContext.Default.UpdateManifest);
        }
        catch (JsonException)
        {
            return null;
        }

        if (manifest is null
            || !AppVersion.TryParse(manifest.Version, out var version)
            || version <= floor
            || (channel == UpdateChannel.Stable && version.IsPreview))
        {
            return null;
        }

        manifest.NotesUrl ??= answer.NotesUrl;
        var notes = BuildNotes(answer.Changelog);
        return new UpdateOffer(version, manifest, manifest.Assets.GetValueOrDefault(target)) { Notes = notes };
    }

    private async Task<ServerUpdateCheckResponse> QueryAsync(string url, string key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (_lastRequestKey == key && _etag is not null && _cached is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
        }

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && _lastRequestKey == key && _cached is not null)
        {
            return _cached;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new UpdateException(UpdateErrorKind.RateLimited, "The update server's request limit was reached.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException(UpdateErrorKind.Network, $"The update check answered {(int)response.StatusCode}.");
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var parsed = await JsonSerializer.DeserializeAsync(stream, UpdatesJsonContext.Default.ServerUpdateCheckResponse, cancellationToken).ConfigureAwait(false)
                ?? new ServerUpdateCheckResponse();
            _lastRequestKey = key;
            _etag = response.Headers.ETag?.ToString();
            _cached = parsed;
            return parsed;
        }
        catch (JsonException e)
        {
            throw new UpdateException(UpdateErrorKind.Network, "The update check response could not be read.", e);
        }
    }

    private static string? BuildNotes(List<ServerUpdateChangelogEntry>? changelog)
    {
        if (changelog is null || changelog.Count == 0) return null;
        var entries = changelog.Take(MaxNotesReleases).ToList();
        var parts = new List<string>();
        foreach (var entry in entries)
        {
            var body = entry.Notes is { } text ? ReleaseNotesTidy.Apply(text) : null;
            if (string.IsNullOrEmpty(body)) continue;
            if (body.Length > MaxNotesChars) body = body[..MaxNotesChars].TrimEnd() + "\n\n…";
            parts.Add(entries.Count > 1 && !string.IsNullOrWhiteSpace(entry.Version) ? $"## {entry.Version}\n\n{body}" : body);
        }
        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException(UpdateErrorKind.Network, "The request timed out.", e);
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException(UpdateErrorKind.Network, e.Message, e);
        }
    }
}
