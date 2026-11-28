using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;

namespace Octopus.Remote.Relay;

/// <summary>
/// Creates the shared <see cref="HttpMessageHandler"/> for relay and mobile HTTP traffic, configures optional
/// WebSocket and QUIC relay TLS root validation, and formats nested transport exceptions so TLS/socket failures on
/// mobile devices retain their root cause.
/// </summary>
public static class RelayHttp
{
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Optional platform override (for example, Android's native <c>AndroidMessageHandler</c>) configured at app
    /// startup before the first HTTP request. Parameter is <c>true</c> when automatic decompression is requested.
    /// </summary>
    public static Func<bool, HttpMessageHandler>? HandlerFactory { get; set; }

    /// <summary>
    /// Optional TLS certificate validation callback applied to outbound <c>wss://</c> WebSocket and <c>quic://</c>
    /// relay tunnel connections (for example, to trust bundled public roots missing from older Android / HarmonyOS
    /// system CA stores).
    /// </summary>
    public static RemoteCertificateValidationCallback? WebSocketCertificateValidationCallback { get; set; }

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

    /// <summary>Parses all PEM-encoded X.509 certificates from <paramref name="pem"/>.</summary>
    public static X509Certificate2Collection LoadPemCertificates(string pem)
    {
        var certificates = new X509Certificate2Collection();
        if (!string.IsNullOrWhiteSpace(pem))
        {
            certificates.ImportFromPem(pem);
        }

        return certificates;
    }

    /// <summary>
    /// Builds a <see cref="RemoteCertificateValidationCallback"/> that falls back to <paramref name="extraRoots"/>
    /// only when the system trust store reports <see cref="SslPolicyErrors.RemoteCertificateChainErrors"/>, never
    /// bypassing hostname or missing-certificate errors.
    /// </summary>
    public static RemoteCertificateValidationCallback CreateCustomRootsValidator(X509Certificate2Collection extraRoots)
    {
        ArgumentNullException.ThrowIfNull(extraRoots);
        var roots = new X509Certificate2[extraRoots.Count];
        for (var i = 0; i < extraRoots.Count; i++)
        {
            roots[i] = extraRoots[i];
        }

        return (_, certificate, chain, sslPolicyErrors) =>
        {
            if (sslPolicyErrors == SslPolicyErrors.None)
            {
                return true;
            }

            if (sslPolicyErrors != SslPolicyErrors.RemoteCertificateChainErrors || certificate is null || roots.Length == 0)
            {
                return false;
            }

            if (certificate is X509Certificate2 existing)
            {
                return ValidateWithCustomRoots(existing, chain, roots);
            }

            using var loadedLeaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            return ValidateWithCustomRoots(loadedLeaf, chain, roots);
        };
    }

    /// <summary>
    /// Applies <see cref="WebSocketCertificateValidationCallback"/> to <paramref name="options"/> for <c>wss://</c>
    /// connections, leaving plaintext <c>ws://</c> connections untouched.
    /// </summary>
    internal static void ConfigureWebSocket(ClientWebSocketOptions options, Uri address)
    {
        if (address.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase)
            && WebSocketCertificateValidationCallback is { } validator)
        {
            options.RemoteCertificateValidationCallback = validator;
        }
    }

    /// <summary>
    /// Applies <see cref="WebSocketCertificateValidationCallback"/> to <paramref name="options"/> for outbound
    /// <c>quic://</c> relay tunnels, leaving standard OS TLS validation active when no custom callback is configured.
    /// </summary>
    internal static void ConfigureQuic(SslClientAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.RemoteCertificateValidationCallback = WebSocketCertificateValidationCallback;
    }

    private static bool ValidateWithCustomRoots(X509Certificate2 leaf, X509Chain? chain, X509Certificate2[] roots)
    {
        using var customChain = new X509Chain();
        customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        customChain.ChainPolicy.CustomTrustStore.AddRange(roots);

        if (chain is not null)
        {
            for (var i = 0; i < chain.ChainElements.Count; i++)
            {
                var elementCert = chain.ChainElements[i].Certificate;
                if (!string.Equals(elementCert.Thumbprint, leaf.Thumbprint, StringComparison.OrdinalIgnoreCase)
                    && !customChain.ChainPolicy.ExtraStore.Contains(elementCert))
                {
                    _ = customChain.ChainPolicy.ExtraStore.Add(elementCert);
                }
            }

            for (var i = 0; i < chain.ChainPolicy.ExtraStore.Count; i++)
            {
                var extraCert = chain.ChainPolicy.ExtraStore[i];
                if (!string.Equals(extraCert.Thumbprint, leaf.Thumbprint, StringComparison.OrdinalIgnoreCase)
                    && !customChain.ChainPolicy.ExtraStore.Contains(extraCert))
                {
                    _ = customChain.ChainPolicy.ExtraStore.Add(extraCert);
                }
            }
        }

        return customChain.Build(leaf);
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
