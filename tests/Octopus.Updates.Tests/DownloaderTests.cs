using System.Net;
using System.Security.Cryptography;

namespace Octopus.Updates.Tests;

public sealed class DownloaderTests : IDisposable
{
    private const string Url = "https://dl.test/1.1.0/Octopus-1.1.0-osx-arm64.zip";
    private readonly FakeHttp _http = new();
    private readonly TempPaths _paths = new();
    private readonly byte[] _content = RandomNumberGenerator.GetBytes(300_000);

    public void Dispose() => _paths.Dispose();

    private string Directory => Path.Combine(_paths.CacheDirectory, "updates", "1.1.0");

    private UpdateAsset Asset(string? sha = null, long? size = null, string name = "Octopus-1.1.0-osx-arm64.zip") => new()
    {
        Name = name,
        Url = Url,
        Size = size ?? _content.Length,
        Sha256 = sha ?? Convert.ToHexStringLower(SHA256.HashData(_content)),
    };

    private Task<string> Download(UpdateAsset asset, IProgress<DownloadProgress>? progress = null) =>
        new UpdateDownloader(new HttpClient(_http)).DownloadAsync(asset, Directory, progress, CancellationToken.None);

    [Fact]
    public async Task DownloadsAndVerifies()
    {
        _http.File(Url, _content);
        var reports = new List<DownloadProgress>();
        var path = await Download(Asset(), new SyncProgress(reports.Add));
        Assert.Equal(_content, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".part"));
        Assert.Equal(_content.Length, reports.Last().Received);
        Assert.Equal(1, reports.Last().Fraction);
    }

    [Fact]
    public async Task AWrongHashIsRejectedAndNothingIsKept()
    {
        _http.File(Url, _content);
        var e = await Assert.ThrowsAsync<UpdateException>(() => Download(Asset(sha: new string('0', 64))));
        Assert.Equal(UpdateErrorKind.Integrity, e.Kind);
        Assert.Empty(System.IO.Directory.EnumerateFiles(Directory));
    }

    [Fact]
    public async Task AWrongSizeIsRejected()
    {
        _http.File(Url, _content);
        var e = await Assert.ThrowsAsync<UpdateException>(() => Download(Asset(size: _content.Length - 1)));
        Assert.Equal(UpdateErrorKind.Integrity, e.Kind);
    }

    [Fact]
    public async Task ADroppedDownloadContinuesWhereItStopped()
    {
        _http.File(Url, _content);
        _http.FailAfterBytes = 100_000;
        var e = await Assert.ThrowsAsync<UpdateException>(() => Download(Asset()));
        Assert.Equal(UpdateErrorKind.Network, e.Kind);
        var part = Path.Combine(Directory, "Octopus-1.1.0-osx-arm64.zip.part");
        Assert.True(File.Exists(part));
        Assert.True(new FileInfo(part).Length >= 100_000 - 65_536);

        _http.FailAfterBytes = null;
        var path = await Download(Asset());
        Assert.Equal(_content, File.ReadAllBytes(path));
        var last = _http.Requests.Last();
        Assert.NotNull(last.Headers.Range);
        Assert.True(last.Headers.Range!.Ranges.First().From > 0);
    }

    [Fact]
    public async Task AServerThatIgnoresRangesStartsOver()
    {
        _http.File(Url, _content);
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllBytes(Path.Combine(Directory, "Octopus-1.1.0-osx-arm64.zip.part"), _content.AsSpan(0, 5000).ToArray());
        _http.IgnoreRange = true;
        var path = await Download(Asset());
        Assert.Equal(_content, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task AFinishedDownloadIsNotFetchedAgain()
    {
        _http.File(Url, _content);
        await Download(Asset());
        var requests = _http.Requests.Count;
        await Download(Asset());
        Assert.Equal(requests, _http.Requests.Count);
    }

    [Fact]
    public async Task ServerErrorsAreNetworkErrors()
    {
        _http.Status(Url, HttpStatusCode.InternalServerError);
        Assert.Equal(UpdateErrorKind.Network, (await Assert.ThrowsAsync<UpdateException>(() => Download(Asset()))).Kind);
    }

    [Theory]
    [InlineData("../evil.zip")]
    [InlineData("a/b.zip")]
    [InlineData("a\\b.zip")]
    [InlineData("..")]
    [InlineData("")]
    public async Task NamesThatAreNotPlainFileNamesAreRefused(string name)
    {
        _http.File(Url, _content);
        var e = await Assert.ThrowsAsync<UpdateException>(() => Download(Asset(name: name)));
        Assert.Equal(UpdateErrorKind.Integrity, e.Kind);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task CancellingStopsAndKeepsThePart()
    {
        _http.File(Url, _content);
        using var cancel = new CancellationTokenSource();
        // The first report comes before any byte is read: cancelling there stops the download midway.
        var progress = new SyncProgress(_ => cancel.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UpdateDownloader(new HttpClient(_http)).DownloadAsync(Asset(), Directory, progress, cancel.Token));
        Assert.False(File.Exists(Path.Combine(Directory, "Octopus-1.1.0-osx-arm64.zip")));
    }

    /// <summary>Unlike Progress&lt;T&gt;, reports on the calling thread at once.</summary>
    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}
