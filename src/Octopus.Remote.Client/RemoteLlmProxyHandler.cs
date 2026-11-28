using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading.Channels;
using Octopus.Remote.Protocol;

namespace Octopus.Remote.Client;

/// <summary>Resolved local provider upstream details (including the real secret from <c>ISecretStore</c>) used exclusively on the controller.</summary>
public sealed record ResolvedTunnelUpstream(
    string Protocol,
    string BaseUrl,
    string ApiKey,
    string? AuthEnvPreference = null,
    IReadOnlyDictionary<string, string>? CustomHeaders = null,
    bool AllowRemoteTunnel = true);

/// <summary>Provides real local provider credentials on demand to <see cref="RemoteLlmProxyHandler"/> without ever sending secrets to remote hosts.</summary>
public interface ILlmTunnelProviderResolver
{
    Task<ResolvedTunnelUpstream?> ResolveUpstreamAsync(string providerId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Controller-side reverse RPC handler for encrypted remote LLM API tunneling (design/20 §6).
/// Manages grant tokens issued to remote hosts, strictly validates incoming paths against SSRF,
/// injects real API keys from the controller's secret store, forwards requests to upstream model providers,
/// and streams SSE/JSON responses back over the authenticated Octopus <see cref="RemoteConnection"/>.
/// </summary>
public sealed class RemoteLlmProxyHandler : IDisposable
{
    private const uint HostChannelBit = 0x8000_0000u;

    private static readonly HashSet<string> BlockedForwardRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host",
        "connection",
        "keep-alive",
        "transfer-encoding",
        "te",
        "trailer",
        "upgrade",
        "proxy-authorization",
        "proxy-connection",
        "content-length",
        "authorization",
        "x-api-key",
        "x-goog-api-key",
        "accept-encoding",
    };

    private static readonly HashSet<string> BlockedForwardResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection",
        "keep-alive",
        "transfer-encoding",
        "te",
        "trailer",
        "upgrade",
        "content-length",
        "content-encoding",
        "set-cookie",
    };

    private readonly ILlmTunnelProviderResolver _resolver;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ConcurrentDictionary<string, GrantRecord> _grants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(RemoteConnection Connection, uint Channel), CancellationTokenSource> _inFlight = new();
    private readonly ConcurrentDictionary<(RemoteConnection Connection, uint Channel), IncomingBodyBuffer> _incomingBodies = new();

    public RemoteLlmProxyHandler(ILlmTunnelProviderResolver resolver, HttpClient? httpClient = null)
    {
        _resolver = resolver;
        if (httpClient is not null)
        {
            _http = httpClient;
            _ownsHttp = false;
        }
        else
        {
            _http = new HttpClient(new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _ownsHttp = true;
        }
    }

    /// <summary>Issues a cryptographically random 256-bit grant token authorizing <paramref name="hostId"/> to tunnel through <paramref name="providerId"/>.</summary>
    public string IssueGrant(string hostId, string providerId)
    {
        if (string.IsNullOrWhiteSpace(hostId)) throw new ArgumentException("Host id is required.", nameof(hostId));
        if (string.IsNullOrWhiteSpace(providerId)) throw new ArgumentException("Provider id is required.", nameof(providerId));
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _grants[token] = new GrantRecord(token, hostId, providerId);
        return token;
    }

    /// <summary>Returns all active grant tokens currently issued for <paramref name="hostId"/>.</summary>
    public IReadOnlyList<string> GrantsForHost(string hostId) =>
        _grants.Values.Where(g => string.Equals(g.HostId, hostId, StringComparison.Ordinal)).Select(g => g.Token).ToList();

    /// <summary>
    /// Attaches reverse LLM proxy handlers to a newly connected <see cref="RemoteHostClient"/> and automatically rebinds
    /// any previously issued grants for <paramref name="hostId"/> so running remote CLIs resume seamlessly after reconnects.
    /// </summary>
    public void Attach(string hostId, RemoteHostClient client)
    {
        var connection = client.Connection;
        var json = RemoteJsonContext.Default;

        connection.Handle(
            RemoteMethods.LlmProxyOpen,
            json.LlmProxyOpenMessage,
            json.LlmProxyOpenResultMessage,
            (request, ct) => HandleOpenAsync(hostId, connection, request, ct));

        connection.On(
            RemoteMethods.LlmProxyCancel,
            json.LlmProxyCancelMessage,
            cancel => CancelCall(connection, cancel.Channel));

        client.LlmTunnelFrameReceived = (header, payload) => OnFrame(connection, header, payload);

        _ = connection.Completion.ContinueWith(_ => CleanupConnection(connection), TaskScheduler.Default);

        // If this host already had active grants from before a disconnect, rebind them immediately.
        var existing = GrantsForHost(hostId);
        if (existing.Count > 0 && client.SupportsLlmTunnel)
        {
            _ = RebindAsync(connection, existing);
        }
    }

    /// <summary>Revokes a single grant locally and notifies the remote host if connected.</summary>
    public async Task RevokeGrantAsync(string grantToken, RemoteHostClient? client = null, CancellationToken cancellationToken = default)
    {
        _grants.TryRemove(grantToken, out _);
        if (client is { SupportsLlmTunnel: true })
        {
            try
            {
                await client.Connection.RequestAsync(
                    RemoteMethods.LlmProxyRevoke,
                    new LlmProxyRevokeMessage { GrantToken = grantToken },
                    RemoteJsonContext.Default.LlmProxyRevokeMessage,
                    RemoteJsonContext.Default.Nothing,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or RemoteException or OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Revokes all grants issued to <paramref name="hostId"/>.</summary>
    public async Task RevokeHostGrantsAsync(string hostId, RemoteHostClient? client = null, CancellationToken cancellationToken = default)
    {
        var tokens = GrantsForHost(hostId);
        foreach (var token in tokens)
        {
            await RevokeGrantAsync(token, client, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RebindAsync(RemoteConnection connection, IReadOnlyList<string> tokens)
    {
        try
        {
            await connection.RequestAsync(
                RemoteMethods.LlmProxyRebind,
                new LlmProxyRebindMessage { GrantTokens = [.. tokens] },
                RemoteJsonContext.Default.LlmProxyRebindMessage,
                RemoteJsonContext.Default.Nothing).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or RemoteException or OperationCanceledException)
        {
        }
    }

    private void OnFrame(RemoteConnection connection, FrameHeader header, ReadOnlyMemory<byte> payload)
    {
        if ((header.Channel & HostChannelBit) == 0) return;
        var key = (connection, header.Channel);
        var buffer = _incomingBodies.GetOrAdd(key, static _ => new IncomingBodyBuffer());
        switch (header.Type)
        {
            case FrameType.StreamData:
                buffer.Push(payload.ToArray());
                break;
            case FrameType.StreamEnd:
                var status = payload.Length > 0 ? payload.Span[0] : (byte)0;
                buffer.Complete(status == 0 ? null : new IOException("Remote request body stream aborted."));
                break;
        }
    }

    private void CancelCall(RemoteConnection connection, uint channel)
    {
        if (_inFlight.TryRemove((connection, channel), out var cts))
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
        if (_incomingBodies.TryRemove((connection, channel), out var buf))
        {
            buf.Complete(new OperationCanceledException());
        }
    }

    private void CleanupConnection(RemoteConnection connection)
    {
        foreach (var key in _inFlight.Keys)
        {
            if (ReferenceEquals(key.Connection, connection) && _inFlight.TryRemove(key, out var cts))
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }
        }
        foreach (var key in _incomingBodies.Keys)
        {
            if (ReferenceEquals(key.Connection, connection) && _incomingBodies.TryRemove(key, out var buf))
            {
                buf.Complete(new IOException("Connection closed."));
            }
        }
    }

    private async Task<LlmProxyOpenResultMessage> HandleOpenAsync(
        string hostId,
        RemoteConnection connection,
        LlmProxyOpenMessage request,
        CancellationToken cancellationToken)
    {
        if ((request.Channel & HostChannelBit) == 0)
        {
            throw new RemoteException(RemoteProtocol.InvalidParams, "LLM tunnel channel must have the host high bit set.");
        }

        if (!_grants.TryGetValue(request.GrantToken, out var grant)
            || !string.Equals(grant.HostId, hostId, StringComparison.Ordinal))
        {
            throw new RemoteException(RemoteMethods.NotAllowed, "Unknown or unauthorized tunnel grant token.");
        }

        var upstream = await _resolver.ResolveUpstreamAsync(grant.ProviderId, cancellationToken).ConfigureAwait(false);
        if (upstream is null || !upstream.AllowRemoteTunnel)
        {
            throw new RemoteException(RemoteMethods.NotAllowed, "The managed provider is disabled or does not allow remote tunneling.");
        }

        if (!TryBuildSafeUpstreamUri(upstream.Protocol, upstream.BaseUrl, request.RelativePath, out var targetUri, out var rejectReason))
        {
            throw new RemoteException(RemoteMethods.NotAllowed, rejectReason ?? "Requested path is not allowed for this provider protocol.");
        }

        var callKey = (connection, request.Channel);
        var callCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_inFlight.TryAdd(callKey, callCts))
        {
            callCts.Dispose();
            throw new RemoteException(RemoteProtocol.InvalidParams, "Tunnel channel is already in use.");
        }

        byte[] bodyBytes;
        try
        {
            if (request.HasBodyStream)
            {
                var buffer = _incomingBodies.GetOrAdd(callKey, static _ => new IncomingBodyBuffer());
                try
                {
                    bodyBytes = await buffer.ReadAllAsync(callCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    _incomingBodies.TryRemove(callKey, out _);
                }
            }
            else if (!string.IsNullOrEmpty(request.InlineBody))
            {
                try { bodyBytes = Convert.FromBase64String(request.InlineBody); }
                catch (FormatException) { throw new RemoteException(RemoteProtocol.InvalidParams, "Invalid inline request body encoding."); }
            }
            else
            {
                bodyBytes = [];
            }
        }
        catch
        {
            CancelCall(connection, request.Channel);
            throw;
        }

        var httpMethod = ParseHttpMethod(request.Method);
        var httpReq = new HttpRequestMessage(httpMethod, targetUri);
        string? contentType = null;

        foreach (var (key, value) in request.Headers)
        {
            if (BlockedForwardRequestHeaders.Contains(key)) continue;
            if (key.Equals("content-type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = value;
                continue;
            }
            httpReq.Headers.TryAddWithoutValidation(key, value);
        }

        // Apply provider custom non-secret headers.
        if (upstream.CustomHeaders is not null)
        {
            foreach (var (key, value) in upstream.CustomHeaders)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                httpReq.Headers.Remove(key);
                httpReq.Headers.TryAddWithoutValidation(key, value);
            }
        }

        // Inject real API key from controller's secret store (never touches remote host).
        ApplyProviderAuthentication(httpReq, upstream);

        if (bodyBytes.Length > 0 || httpMethod == HttpMethod.Post || httpMethod == HttpMethod.Put || httpMethod == HttpMethod.Patch)
        {
            var content = new ByteArrayContent(bodyBytes);
            if (!string.IsNullOrWhiteSpace(contentType) && MediaTypeHeaderValue.TryParse(contentType, out var parsedCt))
            {
                content.Headers.ContentType = parsedCt;
            }
            else if (bodyBytes.Length > 0)
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            httpReq.Content = content;
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, callCts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            httpReq.Dispose();
            CancelCall(connection, request.Channel);
            return new LlmProxyOpenResultMessage
            {
                StatusCode = e is OperationCanceledException ? 499 : 502,
                ErrorMessage = e.Message,
            };
        }

        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            if (BlockedForwardResponseHeaders.Contains(header.Key)) continue;
            responseHeaders[header.Key] = string.Join(", ", header.Value);
        }
        foreach (var header in response.Content.Headers)
        {
            if (BlockedForwardResponseHeaders.Contains(header.Key)) continue;
            responseHeaders[header.Key] = string.Join(", ", header.Value);
        }

        var isEventStream = responseHeaders.TryGetValue("content-type", out var ctHeader)
            && ctHeader.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

        // Start pumping response body over StreamData/StreamEnd immediately after returning response head.
        _ = Task.Run(() => PumpResponseStreamAsync(connection, request.Channel, httpReq, response, isEventStream, callCts));

        return new LlmProxyOpenResultMessage
        {
            StatusCode = (int)response.StatusCode,
            Headers = responseHeaders,
        };
    }

    private async Task PumpResponseStreamAsync(
        RemoteConnection connection,
        uint channel,
        HttpRequestMessage httpReq,
        HttpResponseMessage response,
        bool isEventStream,
        CancellationTokenSource callCts)
    {
        var key = (connection, channel);
        byte endCode = 0;
        try
        {
            await using var responseStream = await response.Content.ReadAsStreamAsync(callCts.Token).ConfigureAwait(false);
            var bufferSize = isEventStream ? 4 * 1024 : 32 * 1024;
            var buffer = new byte[bufferSize];
            while (true)
            {
                var read = await responseStream.ReadAsync(buffer, callCts.Token).ConfigureAwait(false);
                if (read == 0) break;
                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                await connection.SendDataAsync(FrameType.StreamData, channel, chunk, callCts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            endCode = 1;
        }
        finally
        {
            try
            {
                await connection.SendDataAsync(FrameType.StreamEnd, channel, new byte[] { endCode }).ConfigureAwait(false);
            }
            catch
            {
            }
            response.Dispose();
            httpReq.Dispose();
            if (_inFlight.TryRemove(key, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    /// <summary>
    /// Validates <paramref name="relativePath"/> against SSRF / path traversal and protocol allowlists,
    /// and combines it safely with the provider's configured <paramref name="baseUrl"/>.
    /// </summary>
    public static bool TryBuildSafeUpstreamUri(string protocol, string baseUrl, string relativePath, out Uri? targetUri, out string? error)
    {
        targetUri = null;
        error = null;

        if (string.IsNullOrWhiteSpace(baseUrl) && !string.Equals(protocol, "geminiOfficialKey", StringComparison.OrdinalIgnoreCase))
        {
            error = "Provider base URL is not configured.";
            return false;
        }

        var effectiveBase = string.IsNullOrWhiteSpace(baseUrl)
            ? "https://generativelanguage.googleapis.com"
            : baseUrl.Trim();

        if (!Uri.TryCreate(effectiveBase, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttps && baseUri.Scheme != Uri.UriSchemeHttp))
        {
            error = "Provider base URL must be an absolute http(s) URI.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(relativePath) || !relativePath.StartsWith('/'))
        {
            error = "Relative path must start with '/'.";
            return false;
        }

        if (relativePath.StartsWith("//", StringComparison.Ordinal)
            || relativePath.Contains("://", StringComparison.Ordinal)
            || relativePath.Contains('\\')
            || relativePath.Contains('\r')
            || relativePath.Contains('\n')
            || relativePath.Contains('\0'))
        {
            error = "Relative path contains forbidden characters or authority syntax.";
            return false;
        }

        var queryIndex = relativePath.IndexOf('?');
        var pathOnly = queryIndex >= 0 ? relativePath[..queryIndex] : relativePath;
        var query = queryIndex >= 0 ? relativePath[queryIndex..] : "";

        // Reject path traversal segments (including URL-encoded %2e%2e / %2f / %5c).
        var decodedForCheck = Uri.UnescapeDataString(pathOnly);
        if (decodedForCheck.Contains('\\')
            || decodedForCheck.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(static seg => seg is "." or ".."))
        {
            error = "Path traversal is not allowed.";
            return false;
        }

        if (!IsAllowedProtocolPath(protocol, pathOnly))
        {
            error = $"Path '{pathOnly}' is not permitted for protocol '{protocol}'.";
            return false;
        }

        // Combine base path + relative path cleanly, deduplicating overlapping trailing /v1 or /v1beta segment.
        var baseTrimmed = effectiveBase.TrimEnd('/');
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var appendPath = pathOnly;
        if (basePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            && (pathOnly.Equals("/v1", StringComparison.OrdinalIgnoreCase) || pathOnly.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase)))
        {
            appendPath = pathOnly[3..];
        }
        else if (basePath.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase)
                 && (pathOnly.Equals("/v1beta", StringComparison.OrdinalIgnoreCase) || pathOnly.StartsWith("/v1beta/", StringComparison.OrdinalIgnoreCase)))
        {
            appendPath = pathOnly[7..];
        }

        var combined = baseTrimmed + appendPath + query;
        if (!Uri.TryCreate(combined, UriKind.Absolute, out var finalUri)
            || !string.Equals(finalUri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(finalUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
            || finalUri.Port != baseUri.Port)
        {
            error = "Resolved target URI escaped the configured provider origin.";
            return false;
        }

        targetUri = finalUri;
        return true;
    }

    private static bool IsAllowedProtocolPath(string protocol, string pathOnly)
    {
        if (string.Equals(protocol, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return pathOnly.Equals("/v1/messages", StringComparison.OrdinalIgnoreCase)
                || pathOnly.StartsWith("/v1/messages/", StringComparison.OrdinalIgnoreCase)
                || pathOnly.Equals("/v1/models", StringComparison.OrdinalIgnoreCase)
                || pathOnly.StartsWith("/v1/models/", StringComparison.OrdinalIgnoreCase)
                || pathOnly.Equals("/v1/complete", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(protocol, "openaiResponses", StringComparison.OrdinalIgnoreCase)
            || string.Equals(protocol, "openaiChat", StringComparison.OrdinalIgnoreCase))
        {
            return pathOnly.Equals("/chat/completions", StringComparison.OrdinalIgnoreCase)
                || pathOnly.Equals("/v1/chat/completions", StringComparison.OrdinalIgnoreCase)
                || pathOnly.Equals("/responses", StringComparison.OrdinalIgnoreCase)
                || pathOnly.StartsWith("/responses/", StringComparison.OrdinalIgnoreCase)
                || pathOnly.Equals("/v1/responses", StringComparison.OrdinalIgnoreCase)
                || pathOnly.StartsWith("/v1/responses/", StringComparison.OrdinalIgnoreCase)
                || pathOnly.Equals("/models", StringComparison.OrdinalIgnoreCase)
                || pathOnly.StartsWith("/models/", StringComparison.OrdinalIgnoreCase)
                || pathOnly.Equals("/v1/models", StringComparison.OrdinalIgnoreCase)
                || pathOnly.StartsWith("/v1/models/", StringComparison.OrdinalIgnoreCase)
                || pathOnly.Equals("/embeddings", StringComparison.OrdinalIgnoreCase)
                || pathOnly.Equals("/v1/embeddings", StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(protocol, "gemini", StringComparison.OrdinalIgnoreCase)
            || string.Equals(protocol, "geminiOfficialKey", StringComparison.OrdinalIgnoreCase))
        {
            return pathOnly.Equals("/models", StringComparison.OrdinalIgnoreCase)
                || pathOnly.StartsWith("/models/", StringComparison.OrdinalIgnoreCase)
                || pathOnly.Equals("/v1/models", StringComparison.OrdinalIgnoreCase)
                || pathOnly.StartsWith("/v1/models/", StringComparison.OrdinalIgnoreCase)
                || pathOnly.Equals("/v1beta/models", StringComparison.OrdinalIgnoreCase)
                || pathOnly.StartsWith("/v1beta/models/", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static void ApplyProviderAuthentication(HttpRequestMessage request, ResolvedTunnelUpstream upstream)
    {
        if (string.IsNullOrEmpty(upstream.ApiKey)) return;

        if (string.Equals(upstream.Protocol, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(upstream.AuthEnvPreference, "ANTHROPIC_API_KEY", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", upstream.ApiKey);
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", upstream.ApiKey);
            }
            if (!request.Headers.Contains("anthropic-version"))
            {
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            }
        }
        else if (string.Equals(upstream.Protocol, "gemini", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(upstream.Protocol, "geminiOfficialKey", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("x-goog-api-key", upstream.ApiKey);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", upstream.ApiKey);
        }
    }

    private static HttpMethod ParseHttpMethod(string method) => method.ToUpperInvariant() switch
    {
        "GET" => HttpMethod.Get,
        "POST" => HttpMethod.Post,
        "PUT" => HttpMethod.Put,
        "DELETE" => HttpMethod.Delete,
        "PATCH" => HttpMethod.Patch,
        "HEAD" => HttpMethod.Head,
        _ => HttpMethod.Post,
    };

    public void Dispose()
    {
        foreach (var key in _inFlight.Keys)
        {
            if (_inFlight.TryRemove(key, out var cts))
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }
        }
        foreach (var key in _incomingBodies.Keys)
        {
            if (_incomingBodies.TryRemove(key, out var buf))
            {
                buf.Complete(new ObjectDisposedException(nameof(RemoteLlmProxyHandler)));
            }
        }
        if (_ownsHttp) _http.Dispose();
    }

    private sealed record GrantRecord(string Token, string HostId, string ProviderId);

    private sealed class IncomingBodyBuffer
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

        public void Push(byte[] chunk) => _chunks.Writer.TryWrite(chunk);

        public void Complete(Exception? error) => _chunks.Writer.TryComplete(error);

        public async Task<byte[]> ReadAllAsync(CancellationToken cancellationToken)
        {
            using var ms = new MemoryStream();
            await foreach (var chunk in _chunks.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                ms.Write(chunk, 0, chunk.Length);
            }
            return ms.ToArray();
        }
    }
}
