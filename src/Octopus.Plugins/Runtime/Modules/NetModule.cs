using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Octopus.Plugins.Runtime.Modules;

/// <summary>
/// <c>octopus.net.fetch</c> (design/plugins/04-权限与文件系统.md §8, 11-补充能力.md §7.3): https to the hosts the manifest
/// lists. The address is checked when the connection is made, so neither a redirect nor a DNS answer can reach the
/// loopback, link-local or private networks (unless the manifest names <c>localhost</c>). No cookies, no system
/// credentials, 16 MB per response, 60 requests a minute per host, and a 429 pauses that host as long as it says.
/// </summary>
public sealed class NetModule : IPluginApiModule, IDisposable
{
    public const int MaxResponseBytes = 16 * 1024 * 1024;
    public const int MaxRedirects = 5;
    public const int RequestsPerMinute = 60;

    private readonly HttpClient _client;
    private readonly IWebProxy? _proxy;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHost;
    private readonly ConcurrentDictionary<(string Plugin, string Host), RateWindow> _rates = new();

    public NetModule() : this(proxy: null) { }

    internal NetModule(IWebProxy? proxy, Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null)
    {
        _proxy = proxy;
        _resolveHost = resolveHost ?? Dns.GetHostAddressesAsync;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = true,
            Proxy = proxy,
            Credentials = null,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ConnectAsync,
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public void Dispose() => _client.Dispose();

    public void Register(PluginApiRouter router) => router.Request("net.fetch", FetchAsync);

    private Task<PluginReply> FetchAsync(PluginCall call, CancellationToken cancellationToken)
    {
        var grants = call.Plugin.Grants;
        grants.Require("net");
        var hosts = grants.Details("net");
        if (call["auth"] is not null)
        {
            throw PluginApiException.Unsupported("Sending a stored secret with net.fetch");
        }
        var body = call.Text.Length == 0 ? null
            : call.Bool("bodyIsBase64") ? Convert.FromBase64String(call.Text) : Encoding.UTF8.GetBytes(call.Text);
        return FetchCoreAsync(
            call.Plugin.Id,
            hosts,
            call.RequiredString("url"),
            call.String("method"),
            call["headers"] as JsonObject,
            body,
            cancellationToken);
    }

    internal async Task<PluginReply> FetchCoreAsync(
        string pluginId,
        IReadOnlyList<string> hosts,
        string rawUrl,
        string? methodName,
        JsonObject? headers,
        byte[]? body,
        CancellationToken cancellationToken)
    {
        var url = ParseUrl(rawUrl, hosts);
        var methodText = (methodName ?? "GET").ToUpperInvariant();
        if (methodText is "CONNECT" or "TRACE" or "TRACK")
        {
            throw PluginApiException.Invalid($"HTTP method \"{methodText}\" is not allowed.");
        }
        var method = new HttpMethod(methodText);
        var allowLocal = hosts.Contains("localhost", StringComparer.OrdinalIgnoreCase);

        for (var hop = 0; ; hop++)
        {
            if (!allowLocal && IsInternalHost(url))
            {
                throw new PluginApiException("Io", $"{url.Host} resolves only to internal addresses.");
            }
            Throttle(pluginId, url.Host);
            using var request = new HttpRequestMessage(method, url);
            if (body is not null) request.Content = new ByteArrayContent(body);
            if (headers is not null)
            {
                foreach (var (name, value) in headers)
                {
                    if (value is not JsonValue v || !v.TryGetValue<string>(out var text)) continue;
                    if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || name.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!request.Headers.TryAddWithoutValidation(name, text)) request.Content?.Headers.TryAddWithoutValidation(name, text);
                }
            }
            request.Options.Set(PluginKey, hosts);
            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                throw new PluginApiException("Io", e.Message);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new PluginApiException("Timeout", $"Request to {url.Host} timed out.");
            }

            using (response)
            {
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects) throw new PluginApiException("Io", "Too many redirects.");
                    var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                    url = ParseUrl(next.ToString(), hosts);
                    if (response.StatusCode is HttpStatusCode.SeeOther || ((int)response.StatusCode is 301 or 302 && method == HttpMethod.Post))
                    {
                        method = HttpMethod.Get;
                        body = null;
                    }
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(60);
                    _rates.GetOrAdd((pluginId, url.Host), _ => new RateWindow()).PauseUntil = DateTimeOffset.UtcNow + wait;
                }
                byte[] bytes;
                try
                {
                    bytes = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is HttpRequestException or IOException)
                {
                    throw new PluginApiException("Io", e.Message);
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new PluginApiException("Timeout", $"Reading response from {url.Host} timed out.");
                }
                var outHeaders = new JsonObject();
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                {
                    if (header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)) continue;
                    outHeaders[header.Key.ToLowerInvariant()] = string.Join(", ", header.Value);
                }
                return PluginReply.Of(new JsonObject
                {
                    ["status"] = (int)response.StatusCode,
                    ["headers"] = outHeaders,
                    ["body"] = Convert.ToBase64String(bytes),
                });
            }
        }
    }

    private static readonly HttpRequestOptionsKey<IReadOnlyList<string>> PluginKey = new("octopus.plugin.hosts");

    /// <summary>https only (http for an explicitly listed localhost), and the host must be on the list.</summary>
    internal static Uri ParseUrl(string text, IReadOnlyList<string> hosts)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.UserInfo.Length > 0)
        {
            throw PluginApiException.Invalid($"\"{text}\" is not an absolute URL.");
        }
        var localhost = url.IsLoopback && hosts.Contains("localhost", StringComparer.OrdinalIgnoreCase);
        if (url.Scheme != Uri.UriSchemeHttps && !(localhost && url.Scheme == Uri.UriSchemeHttp))
        {
            throw PluginApiException.Invalid("Only https addresses can be fetched.");
        }
        if (!HostAllowed(url.Host, hosts))
        {
            throw PluginApiException.Denied($"net:{url.Host}", canRequest: false);
        }
        return url;
    }

    /// <summary>True when <paramref name="host"/> is on the list (exact, or <c>*.example.com</c> for its sub-domains).</summary>
    public static bool HostAllowed(string host, IReadOnlyList<string> hosts) => hosts.Any(pattern =>
        pattern.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase) && host.Length > pattern.Length - 1
            : string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Connects only to addresses that are not internal, unless the plugin listed localhost or the connection is to the
    /// configured system proxy (which commonly runs on 127.0.0.1 or a LAN address).
    /// </summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var allowLocal = context.InitialRequestMessage.Options.TryGetValue(PluginKey, out var hosts)
            && hosts.Contains("localhost", StringComparer.OrdinalIgnoreCase);
        var proxy = _proxy ?? HttpClient.DefaultProxy;
        var isProxy = IsConfiguredProxyConnection(proxy, context.InitialRequestMessage, context.DnsEndPoint);
        var addresses = await _resolveHost(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        var usable = isProxy || allowLocal ? addresses : addresses.Where(a => !IsInternal(a)).ToArray();
        if (usable.Length == 0)
        {
            throw new HttpRequestException($"{context.DnsEndPoint.Host} resolves only to internal addresses.");
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(usable, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// True when <see cref="SocketsHttpHandler"/> is connecting to the configured proxy rather than the target host.
    /// For HTTPS tunnels through an HTTP proxy, <see cref="SocketsHttpHandler"/> passes an internal
    /// <c>CONNECT</c> request whose <c>RequestUri</c> is the proxy itself and whose <c>Host</c> header is the target.
    /// </summary>
    internal static bool IsConfiguredProxyConnection(IWebProxy? proxy, HttpRequestMessage request, DnsEndPoint endPoint)
    {
        var targetUri = request.RequestUri;
        if (request.Method == HttpMethod.Connect
            && request.Headers.Host is { Length: > 0 } authority
            && Uri.TryCreate($"https://{authority}", UriKind.Absolute, out var tunnelTarget))
        {
            targetUri = tunnelTarget;
        }
        return IsConfiguredProxy(proxy, targetUri, endPoint);
    }

    internal static bool IsConfiguredProxy(IWebProxy? proxy, Uri? requestUri, DnsEndPoint endPoint)
    {
        if (proxy is null || requestUri is null) return false;
        try
        {
            if (proxy.IsBypassed(requestUri)) return false;
            var proxyUri = proxy.GetProxy(requestUri);
            if (proxyUri is null || proxyUri == requestUri || !proxyUri.IsAbsoluteUri) return false;
            var proxyPort = proxyUri.Port > 0
                ? proxyUri.Port
                : proxyUri.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase) ? 1080 : -1;
            if (proxyPort != endPoint.Port) return false;
            return string.Equals(
                TrimBrackets(endPoint.Host),
                TrimBrackets(proxyUri.IdnHost),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static bool IsInternalHost(Uri uri)
    {
        if (uri.IsLoopback) return true;
        var host = TrimBrackets(uri.IdnHost);
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return IPAddress.TryParse(host, out var ip) && IsInternal(ip);
    }

    private static string TrimBrackets(string host) =>
        host.Length >= 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;

    internal static bool IsInternal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal || address.IsIPv6Multicast;
        }
        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || b[0] == 0 || b[0] >= 224;
    }

    private void Throttle(string plugin, string host)
    {
        var window = _rates.GetOrAdd((plugin, host), _ => new RateWindow());
        lock (window)
        {
            var now = DateTimeOffset.UtcNow;
            if (window.PauseUntil > now)
            {
                throw new PluginApiException("Busy", $"{host} asked to slow down.") { RetryAfterMs = (long)(window.PauseUntil - now).TotalMilliseconds };
            }
            window.Times.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
            if (window.Times.Count >= RequestsPerMinute)
            {
                throw new PluginApiException("Busy", $"More than {RequestsPerMinute} requests a minute to {host}.")
                { RetryAfterMs = (long)(window.Times[0] + TimeSpan.FromMinutes(1) - now).TotalMilliseconds };
            }
            window.Times.Add(now);
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new PluginApiException("TooLarge", $"The response is larger than {MaxResponseBytes / 1024 / 1024} MB.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new PluginApiException("TooLarge", $"The response is larger than {MaxResponseBytes / 1024 / 1024} MB.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private sealed class RateWindow
    {
        public List<DateTimeOffset> Times { get; } = [];
        public DateTimeOffset PauseUntil { get; set; }
    }
}
