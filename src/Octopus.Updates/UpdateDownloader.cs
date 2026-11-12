using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Octopus.Updates;

/// <summary>How far a download is.</summary>
/// <param name="BytesPerSecond">Smoothed; 0 until there is enough to measure.</param>
public readonly record struct DownloadProgress(long Received, long Total, double BytesPerSecond)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : 0;

    public TimeSpan? Remaining => BytesPerSecond > 0 && Total > Received
        ? TimeSpan.FromSeconds((Total - Received) / BytesPerSecond)
        : null;
}

/// <summary>
/// Downloads a package into the cache and checks it against the signed manifest (design/10-自动更新.md §7). The bytes go
/// to <c>name.part</c> first and only a file whose size and SHA-256 match takes the real name; an interrupted download
/// continues from where it stopped (HTTP Range).
/// </summary>
public sealed class UpdateDownloader(HttpClient http)
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);
    /// <summary>A connection that sends nothing for this long is given up.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Returns the path of the verified file. Throws <see cref="UpdateException"/> on failure.</summary>
    public async Task<string> DownloadAsync(UpdateAsset asset, string directory, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        Validate(asset);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, asset.Name);
        var part = target + ".part";
        if (File.Exists(target))
        {
            if (await MatchesAsync(target, asset, cancellationToken).ConfigureAwait(false)) return target;
            File.Delete(target);
        }
        try
        {
            await DownloadPartAsync(asset, part, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException e) when (e is not EndOfStreamException)
        {
            throw new UpdateException(UpdateErrorKind.Disk, e.Message, e);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new UpdateException(UpdateErrorKind.Disk, e.Message, e);
        }
        File.Move(part, target, overwrite: true);
        return target;
    }

    private async Task DownloadPartAsync(UpdateAsset asset, string part, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        if (File.Exists(part))
        {
            received = new FileInfo(part).Length;
            if (asset.Size > 0 && received > asset.Size)
            {
                File.Delete(part);
                received = 0;
            }
            else
            {
                await HashFileAsync(part, hash, cancellationToken).ConfigureAwait(false);
            }
        }
        if (asset.Size > 0 && received == asset.Size)
        {
            Finish(part, hash, asset);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        if (received > 0) request.Headers.Range = new RangeHeaderValue(received, null);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException(UpdateErrorKind.Network, e.Message, e);
        }
        using (response)
        {
            if (received > 0 && response.StatusCode == HttpStatusCode.OK)
            {
                // The server ignored the range: start over.
                received = 0;
                hash.GetHashAndReset();
            }
            else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                File.Delete(part);
                throw new UpdateException(UpdateErrorKind.Network, "The download could not be continued; it starts over next time.");
            }
            else if (!response.IsSuccessStatusCode)
            {
                throw new UpdateException(UpdateErrorKind.Network, $"The download answered {(int)response.StatusCode}.");
            }

            var total = asset.Size > 0 ? asset.Size : received + (response.Content.Headers.ContentLength ?? 0);
            await using var output = new FileStream(part, received > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[1 << 16];
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            var lastReceived = received;
            double speed = 0;
            progress?.Report(new DownloadProgress(received, total, 0));
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            while (true)
            {
                idle.CancelAfter(IdleTimeout);
                int read;
                try
                {
                    read = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new UpdateException(UpdateErrorKind.Network, "The download stalled.", e);
                }
                catch (HttpRequestException e)
                {
                    throw new UpdateException(UpdateErrorKind.Network, e.Message, e);
                }
                catch (IOException e)
                {
                    throw new UpdateException(UpdateErrorKind.Network, e.Message, e);
                }
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                received += read;
                if (asset.Size > 0 && received > asset.Size)
                {
                    throw new UpdateException(UpdateErrorKind.Integrity, "The download is larger than the manifest says.");
                }
                var now = clock.Elapsed;
                if (now - lastReport >= ReportInterval)
                {
                    var instant = (received - lastReceived) / (now - lastReport).TotalSeconds;
                    speed = speed == 0 ? instant : speed * 0.8 + instant * 0.2;
                    lastReport = now;
                    lastReceived = received;
                    progress?.Report(new DownloadProgress(received, total, speed));
                }
            }
            progress?.Report(new DownloadProgress(received, total, speed));
        }
        Finish(part, hash, asset);
    }

    /// <summary>Size and hash must match the manifest; a file that does not is deleted (the next try starts over).</summary>
    private static void Finish(string part, IncrementalHash hash, UpdateAsset asset)
    {
        var length = new FileInfo(part).Length;
        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if ((asset.Size > 0 && length != asset.Size) || !string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new UpdateException(UpdateErrorKind.Integrity, $"The downloaded file does not match the release (sha256 {actual}).");
        }
    }

    private static async Task<bool> MatchesAsync(string path, UpdateAsset asset, CancellationToken cancellationToken)
    {
        if (asset.Size > 0 && new FileInfo(path).Length != asset.Size) return false;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await HashFileAsync(path, hash, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task HashFileAsync(string path, IncrementalHash hash, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }
    }

    /// <summary>The manifest is signed, but its file name still becomes a path here: it must be a plain name.</summary>
    private static void Validate(UpdateAsset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.Name)
            || asset.Name != Path.GetFileName(asset.Name)
            || asset.Name is "." or ".."
            || asset.Name.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            throw new UpdateException(UpdateErrorKind.Integrity, $"Not a file name: {asset.Name}");
        }
        if (asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit))
        {
            throw new UpdateException(UpdateErrorKind.Integrity, "The manifest has no valid SHA-256 for the package.");
        }
        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new UpdateException(UpdateErrorKind.Integrity, $"Not a download address: {asset.Url}");
        }
    }
}
