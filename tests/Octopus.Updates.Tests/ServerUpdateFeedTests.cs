using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Octopus.Core.Updates;
using static Octopus.Updates.Tests.Versions;

namespace Octopus.Updates.Tests;

public sealed class ServerUpdateFeedTests : IDisposable
{
    private const string CheckEndpoint = "https://octoterm.si/v1/updates/check";
    private readonly TestKey _key = new();
    private readonly FakeHttp _http = new();

    public void Dispose() => _key.Dispose();

    private ServerUpdateFeed Feed() => new(new HttpClient(_http), new ManifestVerifier([_key.PublicKey]), CheckEndpoint);

    private (byte[] ManifestBytes, string Signature) SignedManifest(string version, string target = "osx-arm64", TestKey? signer = null)
    {
        var payload = Encoding.UTF8.GetBytes($"pkg-{version}");
        var manifest = new UpdateManifest
        {
            Version = version,
            PublishedAt = DateTimeOffset.Parse("2026-11-24T08:00:00Z"),
            NotesUrl = $"https://github.com/AIDotNet/Octopus/releases/tag/v{version}",
            Assets =
            {
                [target] = new UpdateAsset
                {
                    Name = $"Octopus-{version}-{target}.zip",
                    Url = $"https://octoterm.si/releases/download/v{version}/Octopus-{version}-{target}.zip",
                    Size = payload.Length,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(payload)),
                },
            },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, UpdatesJsonContext.Default.UpdateManifest);
        var sig = Encoding.ASCII.GetString((signer ?? _key).Sign(bytes));
        return (bytes, sig);
    }

    [Fact]
    public void DefaultEndpointUsesOctotermSi()
    {
        Assert.Equal("https://octoterm.si", UpdateEndpoints.ServiceUrl);
        Assert.Equal("https://octoterm.si/v1/updates/check", UpdateEndpoints.UpdateCheckUrl);
    }

    [Fact]
    public async Task ReturnsVerifiedOfferWithMergedChangelogAndHonorsEtag()
    {
        var (manifestBytes, sig) = SignedManifest("1.2.0");
        var url = $"{CheckEndpoint}?current=1.0.0&channel=stable&target=osx-arm64";
        var response = new ServerUpdateCheckResponse
        {
            UpdateAvailable = true,
            LatestVersion = "1.2.0",
            Tag = "v1.2.0",
            Version = "1.2.0",
            Channel = "stable",
            NotesUrl = "https://github.com/AIDotNet/Octopus/releases/tag/v1.2.0",
            Manifest = Convert.ToBase64String(manifestBytes),
            Signature = sig,
            Changelog =
            [
                new ServerUpdateChangelogEntry { Version = "1.2.0", Notes = "- New feature" },
                new ServerUpdateChangelogEntry { Version = "1.1.0", Notes = "- Bug fix" },
            ],
        };
        _http.Json(url, JsonSerializer.Serialize(response, UpdatesJsonContext.Default.ServerUpdateCheckResponse), etag: "\"abc123\"");

        var feed = Feed();
        var first = await feed.FindAsync(V("1.0.0"), UpdateChannel.Stable, "osx-arm64", CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal(V("1.2.0"), first.Version);
        Assert.Equal("Octopus-1.2.0-osx-arm64.zip", first.Asset?.Name);
        Assert.Equal("https://octoterm.si/releases/download/v1.2.0/Octopus-1.2.0-osx-arm64.zip", first.Asset?.Url);
        Assert.Contains("## 1.2.0", first.Notes);
        Assert.Contains("## 1.1.0", first.Notes);

        // Second check sends If-None-Match and reuses cached response on 304.
        var second = await feed.FindAsync(V("1.0.0"), UpdateChannel.Stable, "osx-arm64", CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal(V("1.2.0"), second.Version);
        Assert.Contains("\"abc123\"", _http.Requests[1].Headers.GetValues("If-None-Match"));
    }

    [Fact]
    public async Task RejectsForgedSignatureAndPreviewOnStableChannel()
    {
        using var attacker = new TestKey();
        var (badBytes, badSig) = SignedManifest("1.2.0", signer: attacker);
        var stableUrl = $"{CheckEndpoint}?current=1.0.0&channel=stable&target=osx-arm64";
        _http.Json(stableUrl, JsonSerializer.Serialize(new ServerUpdateCheckResponse
        {
            UpdateAvailable = true,
            Version = "1.2.0",
            Manifest = Convert.ToBase64String(badBytes),
            Signature = badSig,
        }, UpdatesJsonContext.Default.ServerUpdateCheckResponse));

        Assert.Null(await Feed().FindAsync(V("1.0.0"), UpdateChannel.Stable, "osx-arm64", CancellationToken.None));

        // Even a validly signed preview manifest is rejected when checking stable channel.
        var (previewBytes, previewSig) = SignedManifest("1.3.0-preview.1");
        _http.Json(stableUrl, JsonSerializer.Serialize(new ServerUpdateCheckResponse
        {
            UpdateAvailable = true,
            Version = "1.3.0-preview.1",
            Manifest = Convert.ToBase64String(previewBytes),
            Signature = previewSig,
        }, UpdatesJsonContext.Default.ServerUpdateCheckResponse));

        Assert.Null(await Feed().FindAsync(V("1.0.0"), UpdateChannel.Stable, "osx-arm64", CancellationToken.None));
    }

    [Fact]
    public async Task Maps429ToRateLimitedAndUpToDateToNull()
    {
        var url = $"{CheckEndpoint}?current=1.2.0&channel=stable&target=win-x64";
        _http.Json(url, JsonSerializer.Serialize(new ServerUpdateCheckResponse
        {
            UpdateAvailable = false,
            LatestVersion = "1.2.0",
        }, UpdatesJsonContext.Default.ServerUpdateCheckResponse));
        Assert.Null(await Feed().FindAsync(V("1.2.0"), UpdateChannel.Stable, "win-x64", CancellationToken.None));

        _http.Status(url, HttpStatusCode.TooManyRequests);
        var ex = await Assert.ThrowsAsync<UpdateException>(() => Feed().FindAsync(V("1.2.0"), UpdateChannel.Stable, "win-x64", CancellationToken.None));
        Assert.Equal(UpdateErrorKind.RateLimited, ex.Kind);
    }
}
