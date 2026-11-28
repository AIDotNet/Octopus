using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Octopus.Agents.Providers;

/// <summary>Upstream CoWork gateway target and short-lived delegated JWT returned by the token supplier.</summary>
public sealed record CoWorkGatewayUpstream(Uri BaseUrl, string DelegatedAccessToken, DateTimeOffset ExpiresAt);

/// <summary>
/// In-process loopback HTTP proxy (<c>http://127.0.0.1:&lt;ephemeral&gt;</c>) that lets any local coding agent CLI
/// (Claude Code, Codex CLI, OpenCode, Aider, etc.) consume CoWork model quota without exposing CoWork refresh tokens,
/// without breaking long sessions when delegated JWTs expire, and while translating <c>x-api-key</c> into
/// <c>Authorization: Bearer</c> for CoWork's gateway.
/// </summary>
public sealed class CoWorkGatewayProxy : IDisposable
{
    private static readonly TimeSpan RefreshLeadTime = TimeSpan.FromMinutes(5);
    private static readonly HashSet<string> HopByHopRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailers",
        "Transfer-Encoding",
        "Upgrade",
        "Authorization",
        "x-api-key",
        "Content-Length",
    };

    private static readonly HashSet<string> HopByHopResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Transfer-Encoding",
        "Upgrade",
        "Content-Length",
    };

    private readonly Func<bool, CancellationToken, Task<CoWorkGatewayUpstream?>> _tokenSupplier;
    private readonly HttpClient _upstreamHttp;
    private readonly bool _ownsHttp;
    private readonly string _userAgent;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly Lock _lifecycleGate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private CoWorkGatewayUpstream? _cachedUpstream;
    private bool _disposed;

    public CoWorkGatewayProxy(
        Func<bool, CancellationToken, Task<CoWorkGatewayUpstream?>> tokenSupplier,
        string userAgent = "Octopus/1.0",
        HttpClient? upstreamHttp = null)
    {
        _tokenSupplier = tokenSupplier ?? throw new ArgumentNullException(nameof(tokenSupplier));
        _userAgent = string.IsNullOrWhiteSpace(userAgent) ? "Octopus/1.0" : userAgent.Trim();
        if (upstreamHttp is not null)
        {
            _upstreamHttp = upstreamHttp;
            _ownsHttp = false;
        }
        else
        {
            _upstreamHttp = new HttpClient(new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.None,
                UseProxy = true,
            })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _ownsHttp = true;
        }

        LocalApiKey = "oct-cw-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    }

    /// <summary>Ephemeral per-process secret injected into local agent child processes (never persisted).</summary>
    public string LocalApiKey { get; }

    /// <summary>Listening port once started; 0 before <see cref="EnsureStarted"/>.</summary>
    public int Port { get; private set; }

    /// <summary>Base URL for Anthropic SDKs (<c>ANTHROPIC_BASE_URL</c>, which append <c>/v1/messages</c>).</summary>
    public string AnthropicBaseUrl => Port > 0 ? $"http://127.0.0.1:{Port}" : "";

    /// <summary>Base URL for OpenAI SDKs (<c>OPENAI_BASE_URL</c>, which append <c>/responses</c> or <c>/chat/completions</c>).</summary>
    public string OpenAiBaseUrl => Port > 0 ? $"http://127.0.0.1:{Port}/v1" : "";

    /// <summary>Starts the loopback listener on a free local port if not already running.</summary>
    public void EnsureStarted()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is { IsListening: true })
            {
                return;
            }

            Exception? lastError = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var port = ReserveFreePort();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    Port = port;
                    _cts = new CancellationTokenSource();
                    _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
                    return;
                }
                catch (HttpListenerException ex)
                {
                    lastError = ex;
                    listener.Close();
                }
            }

            throw new InvalidOperationException("Failed to start local CoWork loopback proxy.", lastError);
        }
    }

    /// <summary>Drops any cached delegated JWT (e.g., on sign-out, unbind, or billing Team switch).</summary>
    public void InvalidateCachedToken()
    {
        _cachedUpstream = null;
    }

    public void Dispose()
    {
        HttpListener? listener;
        CancellationTokenSource? cts;
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            listener = _listener;
            cts = _cts;
            _listener = null;
            _cts = null;
            Port = 0;
        }

        _cachedUpstream = null;
        try { cts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        try { listener?.Close(); } catch { }
        cts?.Dispose();
        _tokenGate.Dispose();
        if (_ownsHttp)
        {
            _upstreamHttp.Dispose();
        }
    }

    private static int ReserveFreePort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        return ((IPEndPoint)tcp.LocalEndpoint).Port;
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context, cancellationToken), cancellationToken);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var req = context.Request;
        var res = context.Response;
        try
        {
            if (!AuthorizeLocalCaller(req))
            {
                await WriteJsonErrorAsync(res, 401, "authentication_error", "invalid_local_token", "Invalid local Octopus proxy token.").ConfigureAwait(false);
                return;
            }

            var upstream = await GetUpstreamAsync(forceRefresh: false, cancellationToken).ConfigureAwait(false);
            if (upstream is null)
            {
                await WriteJsonErrorAsync(res, 401, "authentication_error", "cowork_unavailable", "CoWork is not linked or authorization has expired. Please re-link CoWork in Octopus.").ConfigureAwait(false);
                return;
            }

            byte[]? requestBody = null;
            if (req.HasEntityBody)
            {
                using var ms = new MemoryStream();
                await req.InputStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                requestBody = ms.ToArray();
            }

            using var firstResponse = await ForwardOnceAsync(req, requestBody, upstream, cancellationToken).ConfigureAwait(false);
            var finalResponse = firstResponse;
            HttpResponseMessage? retryResponse = null;

            if (firstResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                var refreshed = await GetUpstreamAsync(forceRefresh: true, cancellationToken).ConfigureAwait(false);
                if (refreshed is not null)
                {
                    retryResponse = await ForwardOnceAsync(req, requestBody, refreshed, cancellationToken).ConfigureAwait(false);
                    finalResponse = retryResponse;
                }
            }

            try
            {
                await StreamResponseAsync(res, finalResponse, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                retryResponse?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            try
            {
                await WriteJsonErrorAsync(res, 502, "api_error", "proxy_error", $"Failed to reach CoWork gateway: {ex.Message}").ConfigureAwait(false);
            }
            catch
            {
            }
        }
        finally
        {
            try { res.OutputStream.Close(); } catch { }
        }
    }

    private bool AuthorizeLocalCaller(HttpListenerRequest req)
    {
        var presented = req.Headers["x-api-key"];
        if (string.IsNullOrEmpty(presented))
        {
            var auth = req.Headers["Authorization"];
            if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                presented = auth[7..].Trim();
            }
        }

        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(LocalApiKey);
        var actualBytes = Encoding.UTF8.GetBytes(presented);
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private async Task<CoWorkGatewayUpstream?> GetUpstreamAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var current = _cachedUpstream;
        if (!forceRefresh && current is not null && current.ExpiresAt > now + RefreshLeadTime)
        {
            return current;
        }

        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            current = _cachedUpstream;
            if (!forceRefresh && current is not null && current.ExpiresAt > now + RefreshLeadTime)
            {
                return current;
            }

            var fresh = await _tokenSupplier(forceRefresh, cancellationToken).ConfigureAwait(false);
            _cachedUpstream = fresh;
            return fresh;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private Task<HttpResponseMessage> ForwardOnceAsync(
        HttpListenerRequest req,
        byte[]? requestBody,
        CoWorkGatewayUpstream upstream,
        CancellationToken cancellationToken)
    {
        var rawPathAndQuery = req.RawUrl ?? "/";
        var targetUri = new Uri(upstream.BaseUrl, rawPathAndQuery);
        var outbound = new HttpRequestMessage(new HttpMethod(req.HttpMethod), targetUri);
        outbound.Headers.Authorization = new AuthenticationHeaderValue("Bearer", upstream.DelegatedAccessToken);
        outbound.Headers.TryAddWithoutValidation("User-Agent", _userAgent);

        if (requestBody is not null)
        {
            outbound.Content = new ByteArrayContent(requestBody);
            if (!string.IsNullOrWhiteSpace(req.ContentType))
            {
                outbound.Content.Headers.TryAddWithoutValidation("Content-Type", req.ContentType);
            }
        }

        foreach (var headerName in req.Headers.AllKeys)
        {
            if (string.IsNullOrEmpty(headerName) || HopByHopRequestHeaders.Contains(headerName))
            {
                continue;
            }

            if (headerName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                || headerName.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var values = req.Headers.GetValues(headerName);
            if (values is null) continue;
            if (!outbound.Headers.TryAddWithoutValidation(headerName, values))
            {
                outbound.Content?.Headers.TryAddWithoutValidation(headerName, values);
            }
        }

        return _upstreamHttp.SendAsync(outbound, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static async Task StreamResponseAsync(
        HttpListenerResponse res,
        HttpResponseMessage upstream,
        CancellationToken cancellationToken)
    {
        res.StatusCode = (int)upstream.StatusCode;

        foreach (var header in upstream.Headers)
        {
            if (HopByHopResponseHeaders.Contains(header.Key)) continue;
            foreach (var value in header.Value)
            {
                try { res.Headers.Add(header.Key, value); } catch { }
            }
        }

        foreach (var header in upstream.Content.Headers)
        {
            if (HopByHopResponseHeaders.Contains(header.Key)) continue;
            if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                res.ContentType = string.Join(", ", header.Value);
                continue;
            }

            foreach (var value in header.Value)
            {
                try { res.Headers.Add(header.Key, value); } catch { }
            }
        }

        var isEventStream = res.ContentType?.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
        if (!isEventStream && upstream.Content.Headers.ContentLength is { } contentLength)
        {
            res.ContentLength64 = contentLength;
        }
        else
        {
            res.SendChunked = true;
        }

        await using var source = await upstream.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read <= 0) break;
            await res.OutputStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (isEventStream)
            {
                await res.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await res.OutputStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonErrorAsync(HttpListenerResponse res, int status, string type, string code, string message)
    {
        var escapedMessage = message.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        var payload = Encoding.UTF8.GetBytes($"{{\"error\":{{\"type\":\"{type}\",\"code\":\"{code}\",\"message\":\"{escapedMessage}\"}}}}");
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = payload.Length;
        await res.OutputStream.WriteAsync(payload).ConfigureAwait(false);
        await res.OutputStream.FlushAsync().ConfigureAwait(false);
    }
}
