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
    private readonly ConcurrentDictionary<(string Plugin, string Host), RateWindow> _rates = new();

    public NetModule()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = true,
            Credentials = null,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ConnectAsync,
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public void Dispose() => _client.Dispose();

    public void Register(PluginApiRouter router) => router.Request("net.fetch", FetchAsync);

    private async Task<PluginReply> FetchAsync(PluginCall call, CancellationToken cancellationToken)
    {
        var grants = call.Plugin.Grants;
        grants.Require("net");
        var hosts = grants.Details("net");
        if (call["auth"] is not null)
        {
            throw PluginApiException.Unsupported("Sending a stored secret with net.fetch");
        }
        var url = ParseUrl(call.RequiredString("url"), hosts);
        var method = new HttpMethod((call.String("method") ?? "GET").ToUpperInvariant());
        byte[]? body = call.Text.Length == 0 ? null
            : call.Bool("bodyIsBase64") ? Convert.FromBase64String(call.Text) : Encoding.UTF8.GetBytes(call.Text);

        for (var hop = 0; ; hop++)
        {
            Throttle(call.Plugin.Id, url.Host);
            using var request = new HttpRequestMessage(method, url);
            if (body is not null) request.Content = new ByteArrayContent(body);
            if (call["headers"] is JsonObject headers)
            {
                foreach (var (name, value) in headers)
                {
                    if (value is not JsonValue v || !v.TryGetValue<string>(out var text)) continue;
                    if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || name.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!request.Headers.TryAddWithoutValidation(name, text)) request.Content?.Headers.TryAddWithoutValidation(name, text);
                }
            }
            request.Options.Set(PluginKey, hosts);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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
                _rates.GetOrAdd((call.Plugin.Id, url.Host), _ => new RateWindow()).PauseUntil = DateTimeOffset.UtcNow + wait;
            }
            var bytes = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Connects only to addresses that are not internal, unless the plugin listed localhost.</summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var allowLocal = context.InitialRequestMessage.Options.TryGetValue(PluginKey, out var hosts)
            && hosts.Contains("localhost", StringComparer.OrdinalIgnoreCase);
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        var usable = addresses.Where(a => allowLocal || !IsInternal(a)).ToList();
        if (usable.Count == 0)
        {
            throw new HttpRequestException($"{context.DnsEndPoint.Host} resolves only to internal addresses.");
        }
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(usable.ToArray(), context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

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
