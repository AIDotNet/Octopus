using System.Net;
using System.Text;
using Octopus.Core.Updates;
using static Octopus.Updates.Tests.Versions;

namespace Octopus.Updates.Tests;

public sealed class FeedTests : IDisposable
{
    private readonly TestKey _key = new();
    private readonly FakeHttp _http = new();
    private readonly FakeReleases _releases;
    private readonly GitHubReleasesFeed _feed;

    public FeedTests()
    {
        _releases = new FakeReleases(_http, _key);
        _feed = new GitHubReleasesFeed(new HttpClient(_http), new ManifestVerifier([_key.PublicKey]), FakeReleases.ListUrl);
    }

    public void Dispose() => _key.Dispose();

    private Task<UpdateOffer?> Find(string floor, UpdateChannel channel = UpdateChannel.Stable, string target = "osx-arm64") =>
        _feed.FindAsync(V(floor), channel, target, CancellationToken.None);

    [Fact]
    public async Task FindsTheNewestRelease()
    {
        _releases.Add("1.0.0");
        _releases.Add("1.1.0");
        _releases.Add("1.0.5");
        var offer = await Find("1.0.0");
        Assert.NotNull(offer);
        Assert.Equal(V("1.1.0"), offer.Version);
        Assert.Equal("Octopus-1.1.0-osx-arm64.zip", offer.Asset!.Name);
        Assert.Equal("https://github.test/o/r/releases/tag/v1.1.0", offer.NotesUrl);
    }

    [Fact]
    public async Task NothingNewerIsNull()
    {
        _releases.Add("1.0.0");
        Assert.Null(await Find("1.0.0"));
        Assert.Null(await Find("2.0.0"));
    }

    [Fact]
    public async Task TheStableChannelSkipsPreviews()
    {
        _releases.Add("1.0.0");
        _releases.Add("1.1.0-preview.1");
        Assert.Null(await Find("1.0.0", UpdateChannel.Stable));
        Assert.Equal(V("1.1.0-preview.1"), (await Find("1.0.0", UpdateChannel.Preview))!.Version);
    }

    [Fact]
    public async Task ThePreviewChannelMovesOnToTheRelease()
    {
        _releases.Add("1.1.0-preview.1");
        _releases.Add("1.1.0-preview.2");
        _releases.Add("1.1.0");
        Assert.Equal(V("1.1.0"), (await Find("1.1.0-preview.1", UpdateChannel.Preview))!.Version);
    }

    [Fact]
    public async Task DraftsAndOddTagsAreIgnored()
    {
        _releases.Add("1.0.0");
        _releases.Add("1.2.0", draft: true);
        _http.Json(FakeReleases.ListUrl, """[{"tag_name":"nightly","draft":false,"assets":[]},{"tag_name":"v1.0.0","draft":false,"assets":[]}]""");
        Assert.Null(await Find("0.9.0"));
    }

    [Fact]
    public async Task AForgedManifestIsSkippedForTheNextRelease()
    {
        using var other = new TestKey();
        _releases.Add("1.1.0");
        _releases.Add("1.2.0", signedBy: other);
        Assert.Equal(V("1.1.0"), (await Find("1.0.0"))!.Version);
    }

    [Fact]
    public async Task AManifestOfAnotherVersionIsNotThisRelease()
    {
        _releases.Add("1.1.0");
        _releases.Add("1.2.0", manifestVersion: "1.1.0");
        Assert.Equal(V("1.1.0"), (await Find("1.0.0"))!.Version);
    }

    [Fact]
    public async Task AReleaseWithoutAManifestIsSkipped()
    {
        _releases.Add("1.1.0");
        _releases.Add("1.2.0", withManifest: false);
        Assert.Equal(V("1.1.0"), (await Find("1.0.0"))!.Version);
    }

    [Fact]
    public async Task AReleaseWithoutThisPlatformIsOfferedWithoutAPackage()
    {
        _releases.Add("1.1.0", target: "win-x64");
        var offer = await Find("1.0.0");
        Assert.NotNull(offer);
        Assert.Null(offer.Asset);
    }

    [Fact]
    public async Task UnchangedListsAreAnsweredFromTheCache()
    {
        _releases.Add("1.1.0");
        var json = await new HttpClient(_http).GetStringAsync(FakeReleases.ListUrl);
        _http.Json(FakeReleases.ListUrl, json, etag: "\"abc\"");
        Assert.NotNull(await Find("1.0.0"));
        Assert.NotNull(await Find("1.0.0"));
        var listRequests = _http.Requests.Where(r => r.RequestUri!.ToString() == FakeReleases.ListUrl).ToList();
        Assert.Contains("\"abc\"", listRequests.Last().Headers.GetValues("If-None-Match"));
    }

    [Fact]
    public async Task TheRateLimitIsReportedAsSuch()
    {
        _http.Status(FakeReleases.ListUrl, HttpStatusCode.Forbidden, r => r.Headers.Add("X-RateLimit-Remaining", "0"));
        var e = await Assert.ThrowsAsync<UpdateException>(() => Find("1.0.0"));
        Assert.Equal(UpdateErrorKind.RateLimited, e.Kind);
    }

    [Fact]
    public async Task ServerErrorsAreNetworkErrors()
    {
        _http.Status(FakeReleases.ListUrl, HttpStatusCode.BadGateway);
        Assert.Equal(UpdateErrorKind.Network, (await Assert.ThrowsAsync<UpdateException>(() => Find("1.0.0"))).Kind);
        _http.Json(FakeReleases.ListUrl, "{not json");
        Assert.Equal(UpdateErrorKind.Network, (await Assert.ThrowsAsync<UpdateException>(() => Find("1.0.0"))).Kind);
    }
}

public sealed class ManifestVerifierTests : IDisposable
{
    private readonly TestKey _key = new();
    private readonly byte[] _data = Encoding.UTF8.GetBytes("""{"version":"1.2.3"}""");

    public void Dispose() => _key.Dispose();

    [Fact]
    public void AcceptsTheKeysSignature() => Assert.True(new ManifestVerifier([_key.PublicKey]).Verify(_data, _key.Sign(_data)));

    [Fact]
    public void AcceptsRawDerAndPemKeys()
    {
        var der = Convert.FromBase64String(Encoding.ASCII.GetString(_key.Sign(_data)));
        var pem = "-----BEGIN PUBLIC KEY-----\n" + _key.PublicKey + "\n-----END PUBLIC KEY-----\n";
        Assert.True(new ManifestVerifier([pem]).Verify(_data, der));
    }

    [Fact]
    public void RejectsChangedData()
    {
        var signature = _key.Sign(_data);
        var changed = Encoding.UTF8.GetBytes("""{"version":"1.2.4"}""");
        Assert.False(new ManifestVerifier([_key.PublicKey]).Verify(changed, signature));
    }

    [Fact]
    public void RejectsOtherKeysAndGarbage()
    {
        using var other = new TestKey();
        var verifier = new ManifestVerifier([_key.PublicKey]);
        Assert.False(verifier.Verify(_data, other.Sign(_data)));
        Assert.False(verifier.Verify(_data, "not base64!"u8));
        Assert.False(verifier.Verify(_data, []));
    }

    [Fact]
    public void AnyTrustedKeyWillDo()
    {
        using var next = new TestKey();
        Assert.True(new ManifestVerifier([_key.PublicKey, next.PublicKey]).Verify(_data, next.Sign(_data)));
    }

    [Fact]
    public void ABadKeyFailsAtOnce()
    {
        Assert.ThrowsAny<Exception>(() => new ManifestVerifier(["AAAA"]));
        Assert.Throws<ArgumentException>(() => new ManifestVerifier([]));
    }

    [Fact]
    public void TheShippedKeyLoads() => _ = new ManifestVerifier(UpdateEndpoints.PublicKeys);

    /// <summary>What the release tooling writes (openssl's DER, base64) is what the app accepts, and parses.</summary>
    [Fact]
    public void AcceptsWhatTheReleaseToolingSigns()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var data = File.ReadAllBytes(Path.Combine(dir, "update.json"));
        var signature = File.ReadAllBytes(Path.Combine(dir, "update.json.sig"));
        var verifier = new ManifestVerifier([File.ReadAllText(Path.Combine(dir, "openssl-key.pub"))]);
        Assert.True(verifier.Verify(data, signature));
        Assert.False(new ManifestVerifier(UpdateEndpoints.PublicKeys).Verify(data, signature));

        var manifest = System.Text.Json.JsonSerializer.Deserialize(data, UpdatesJsonContext.Default.UpdateManifest)!;
        Assert.Equal("1.3.1201-preview.2", manifest.Version);
        Assert.Equal(["linux-x64", "osx-arm64", "osx-x64", "win-x64"], manifest.Assets.Keys.Order());
        Assert.EndsWith("-win-x64-setup.exe", manifest.Assets["win-x64"].Name);
        Assert.Equal(64, manifest.Assets["osx-arm64"].Sha256.Length);
    }
}
