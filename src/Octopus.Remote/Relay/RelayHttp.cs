using System.Net;

namespace Octopus.Remote.Relay;

/// <summary>
/// Creates the shared <see cref="HttpMessageHandler"/> for relay and mobile HTTP traffic, and formats nested transport
/// exceptions so TLS/socket failures on mobile devices retain their root cause.
/// </summary>
public static class RelayHttp
{
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Optional platform override (for example, Android's native <c>AndroidMessageHandler</c>) configured at app
    /// startup before the first HTTP request. Parameter is <c>true</c> when automatic decompression is requested.
    /// </summary>
    public static Func<bool, HttpMessageHandler>? HandlerFactory { get; set; }

    /// <summary>Creates an <see cref="HttpMessageHandler"/> appropriate for the current platform.</summary>
    public static HttpMessageHandler CreateHandler(bool automaticDecompression = false)
    {
        if (HandlerFactory is { } factory)
        {
            return factory(automaticDecompression);
        }

        if (OperatingSystem.IsAndroid())
        {
            return new HttpClientHandler
            {
                AutomaticDecompression = automaticDecompression ? DecompressionMethods.All : DecompressionMethods.None,
            };
        }

        return new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = DefaultConnectTimeout,
            AutomaticDecompression = automaticDecompression ? DecompressionMethods.All : DecompressionMethods.None,
        };
    }

    /// <summary>
    /// Formats an exception together with its inner cause chain so trimmed/mobile builds do not hide the underlying
    /// TLS, certificate, or socket error behind a generic outer message.
    /// </summary>
    public static string DescribeError(Exception error)
    {
        var parts = new List<string>(4);
        var current = error;
        for (var depth = 0; current is not null && depth < 4; depth++, current = current.InnerException)
        {
            var message = current.Message?.Trim();
            if (string.IsNullOrEmpty(message))
            {
                message = current.GetType().Name;
            }
            else if (depth > 0)
            {
                var typeName = current.GetType().Name;
                if (!message.StartsWith(typeName, StringComparison.Ordinal))
                {
                    message = $"{typeName}: {message}";
                }
            }

            if (parts.Count == 0 || !string.Equals(parts[^1], message, StringComparison.Ordinal))
            {
                parts.Add(message);
            }
        }

        return parts.Count > 0 ? string.Join(" -> ", parts) : error.GetType().Name;
    }
}
