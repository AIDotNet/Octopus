using System.Net;
using System.Net.Sockets;
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

    [Fact]
    public async Task ProgressIsReportedAtMostOncePerSecond()
    {
        var clock = new StepTimeProvider();
        // 5 chunks of 60_000 bytes; each read advances the clock by 400 ms.
        // Expected ticks: initial (0), chunk 3 at 1.2s (180_000), final on completion (300_000).
        using var http = new HttpClient(new ChunkedHttp(_content, chunkSize: 60_000, onRead: () => clock.Advance(TimeSpan.FromMilliseconds(400))));
        var reports = new List<DownloadProgress>();
        var downloader = new UpdateDownloader(http, clock);
        await downloader.DownloadAsync(Asset(), Directory, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal([0, 180_000, 300_000], reports.Select(r => r.Received).ToArray());
        Assert.Equal(0, reports[0].BytesPerSecond);
        Assert.Equal(180_000 / 1.2, reports[1].BytesPerSecond, precision: 3);
    }

    [Fact]
    public async Task PrematureEofWithContentLengthIsNetworkErrorAndKeepsPartForResume()
    {
        var handler = new TruncatingThenResumingHttp(_content, cutAt: 100_000);
        using var http = new HttpClient(handler);
        var downloader = new UpdateDownloader(http);

        var e = await Assert.ThrowsAsync<UpdateException>(() => downloader.DownloadAsync(Asset(), Directory, null, CancellationToken.None));
        Assert.Equal(UpdateErrorKind.Network, e.Kind);
        var part = Path.Combine(Directory, "Octopus-1.1.0-osx-arm64.zip.part");
        Assert.True(File.Exists(part));
        Assert.Equal(100_000, new FileInfo(part).Length);

        // Retry resumes from the saved .part via HTTP Range and completes cleanly.
        var path = await downloader.DownloadAsync(Asset(), Directory, null, CancellationToken.None);
        Assert.Equal(_content, File.ReadAllBytes(path));
        Assert.Equal(100_000, handler.LastRangeFrom);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MidStreamSocketAndDisposedErrorsMapToNetworkErrorAndKeepPart(bool throwDisposed)
    {
        using var http = new HttpClient(new FaultingStreamHttp(
            _content,
            failAfter: 80_000,
            errorFactory: () => throwDisposed
                ? new ObjectDisposedException("SslStream")
                : new SocketException((int)SocketError.ConnectionReset)));
        var downloader = new UpdateDownloader(http);

        var e = await Assert.ThrowsAsync<UpdateException>(() => downloader.DownloadAsync(Asset(), Directory, null, CancellationToken.None));
        Assert.Equal(UpdateErrorKind.Network, e.Kind);
        var part = Path.Combine(Directory, "Octopus-1.1.0-osx-arm64.zip.part");
        Assert.True(File.Exists(part));
        Assert.True(new FileInfo(part).Length > 0);
    }

    private sealed class TruncatingThenResumingHttp(byte[] content, int cutAt) : HttpMessageHandler
    {
        private int _calls;
        public long? LastRangeFrom { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _calls++;
            var from = (int)(request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0);
            LastRangeFrom = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
            if (_calls == 1)
            {
                // Advertises the full Content-Length, but closes the stream early at cutAt (simulating TCP FIN on network drop).
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new MemoryStream(content, 0, cutAt, writable: false)),
                };
                response.Content.Headers.ContentLength = content.Length;
                return Task.FromResult(response);
            }

            var remaining = content.Length - from;
            var partial = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(content, from, remaining),
            };
            partial.Content.Headers.ContentLength = remaining;
            partial.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, content.Length - 1, content.Length);
            return Task.FromResult(partial);
        }
    }

    private sealed class FaultingStreamHttp(byte[] content, int failAfter, Func<Exception> errorFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FaultingStream(content, failAfter, errorFactory)),
            };
            response.Content.Headers.ContentLength = content.Length;
            return Task.FromResult(response);
        }

        private sealed class FaultingStream(byte[] data, int failAfter, Func<Exception> errorFactory) : MemoryStream(data)
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (Position >= failAfter) throw errorFactory();
                var count = (int)Math.Min(Math.Min(buffer.Length, 32_768), failAfter - Position);
                return base.ReadAsync(buffer[..count], cancellationToken);
            }
        }
    }

    /// <summary>Unlike Progress&lt;T&gt;, reports on the calling thread at once.</summary>
    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    private sealed class StepTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan delta) => _ticks += delta.Ticks;
    }

    private sealed class ChunkedHttp(byte[] content, int chunkSize, Action onRead) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ChunkedStream(content, chunkSize, onRead)),
            };
            response.Content.Headers.ContentLength = content.Length;
            return Task.FromResult(response);
        }

        private sealed class ChunkedStream(byte[] data, int chunkSize, Action onRead) : MemoryStream(data)
        {
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (Position >= Length) return ValueTask.FromResult(0);
                onRead();
                var count = (int)Math.Min(Math.Min(buffer.Length, chunkSize), Length - Position);
                return base.ReadAsync(buffer[..count], cancellationToken);
            }
        }
    }
}
