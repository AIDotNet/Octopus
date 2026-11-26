using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Octopus.Acp.Transport;

/// <summary>
/// ACP Remote Streamable HTTP transport (<c>http://</c> / <c>https://</c>):
/// sends client JSON-RPC payloads via <c>POST</c> (accepting JSON or inline SSE <c>text/event-stream</c> replies)
/// and optionally listens on a background <c>GET</c> SSE stream for server-pushed notifications and requests.
/// </summary>
public sealed class AcpStreamableHttpTransport : IAcpTransport
{
    private readonly Uri _endpoint;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly IReadOnlyDictionary<string, string>? _headers;
    private readonly bool _enableSseStream;
    private readonly Channel<IncomingItem> _incoming = Channel.CreateUnbounded<IncomingItem>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private volatile string? _sessionHeader;
    private int _disposed;

    public AcpStreamableHttpTransport(
        Uri endpoint,
        IReadOnlyDictionary<string, string>? headers = null,
        HttpClient? httpClient = null,
        bool enableSseStream = true,
        int maxLineBytes = NdjsonLineFramer.DefaultMaxLineBytes)
    {
        if (endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException($"ACP HTTP endpoint must use http:// or https:// scheme (got '{endpoint.Scheme}').", nameof(endpoint));
        }
        _endpoint = endpoint;
        _headers = headers;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _enableSseStream = enableSseStream;
        MaxLineBytes = maxLineBytes;
    }

    public int MaxLineBytes { get; }

    /// <summary>Session header negotiated with the remote ACP endpoint (e.g. <c>Acp-Session-Id</c> or <c>Mcp-Session-Id</c>).</summary>
    public string? RemoteSessionHeader => _sessionHeader;

    public async Task SendAsync(string jsonLine, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(jsonLine, Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyHeaders(req);

        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        CaptureSessionHeader(resp);

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = "";
            try { errBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); } catch { }
            resp.Dispose();
            throw new AcpException($"ACP HTTP POST failed with {(int)resp.StatusCode} {resp.ReasonPhrase}: {errBody}".Trim());
        }

        if (resp.StatusCode == HttpStatusCode.Accepted || resp.StatusCode == HttpStatusCode.NoContent)
        {
            resp.Dispose();
            return;
        }

        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            // Stream POST-bound SSE events asynchronously so the writer loop is never blocked by long-running turns.
            _ = Task.Run(async () =>
            {
                using (resp)
                {
                    try
                    {
                        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                        await ReadSseStreamAsync(stream, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _incoming.Writer.TryWrite(new IncomingItem(null, $"ACP HTTP SSE response stream ended: {ex.Message}", null));
                    }
                }
            }, CancellationToken.None);
            return;
        }

        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(body))
            {
                EnqueuePayload(body);
            }
        }
    }

    public async Task RunReceiveLoopAsync(
        Action<JsonDocument> onMessage,
        Action<string> onOtherLine,
        Action<OversizedAcpLine> onOversized,
        CancellationToken cancellationToken)
    {
        Task? sseTask = null;
        if (_enableSseStream)
        {
            sseTask = Task.Run(() => ListenBackgroundSseAsync(cancellationToken), CancellationToken.None);
        }

        try
        {
            await foreach (var item in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (item.Doc is not null)
                {
                    onMessage(item.Doc);
                }
                else if (item.Oversized is not null)
                {
                    onOversized(item.Oversized);
                }
                else if (!string.IsNullOrEmpty(item.OtherLine))
                {
                    onOtherLine(item.OtherLine);
                }
            }
        }
        finally
        {
            if (sseTask is not null)
            {
                try { await sseTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _incoming.Writer.TryComplete();
        if (_ownsHttp)
        {
            _http.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private async Task ListenBackgroundSseAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _endpoint);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            ApplyHeaders(req);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            CaptureSessionHeader(resp);
            if (resp.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound)
            {
                // Server only supports POST responses (inline JSON / SSE) without a standalone GET SSE stream.
                return;
            }
            if (!resp.IsSuccessStatusCode)
            {
                return;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await ReadSseStreamAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Background GET SSE stream is best-effort; POST replies continue functioning.
        }
    }

    private async Task ReadSseStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var dataLines = new StringBuilder();
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;

            if (line.Length == 0)
            {
                if (dataLines.Length > 0)
                {
                    EnqueuePayload(dataLines.ToString());
                    dataLines.Clear();
                }
                continue;
            }
            if (line.StartsWith(':')) continue; // SSE comment / heartbeat
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..];
                if (dataLines.Length > 0) dataLines.Append('\n');
                dataLines.Append(value);
            }
        }
        if (dataLines.Length > 0)
        {
            EnqueuePayload(dataLines.ToString());
        }
    }

    private void EnqueuePayload(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0) return;

        var byteCount = Encoding.UTF8.GetByteCount(trimmed);
        if (byteCount > MaxLineBytes)
        {
            var prefix = trimmed.Length > 4096 ? trimmed[..4096] : trimmed;
            _incoming.Writer.TryWrite(new IncomingItem(null, null, new OversizedAcpLine(prefix, byteCount, NdjsonLineFramer.TryExtractId(prefix))));
            return;
        }

        try
        {
            var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                _incoming.Writer.TryWrite(new IncomingItem(doc, null, null));
                return;
            }
            doc.Dispose();
        }
        catch (JsonException) { }

        _incoming.Writer.TryWrite(new IncomingItem(null, trimmed, null));
    }

    private void ApplyHeaders(HttpRequestMessage req)
    {
        if (_headers is not null)
        {
            foreach (var (k, v) in _headers)
            {
                if (!string.IsNullOrWhiteSpace(k))
                {
                    req.Headers.TryAddWithoutValidation(k, v);
                }
            }
        }
        if (!string.IsNullOrEmpty(_sessionHeader))
        {
            req.Headers.TryAddWithoutValidation("Acp-Session-Id", _sessionHeader);
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionHeader);
        }
    }

    private void CaptureSessionHeader(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("Acp-Session-Id", out var acpVals) && acpVals.FirstOrDefault() is { Length: > 0 } acpId)
        {
            _sessionHeader = acpId;
        }
        else if (resp.Headers.TryGetValues("Mcp-Session-Id", out var mcpVals) && mcpVals.FirstOrDefault() is { Length: > 0 } mcpId)
        {
            _sessionHeader = mcpId;
        }
    }

    private sealed record IncomingItem(JsonDocument? Doc, string? OtherLine, OversizedAcpLine? Oversized);
}
