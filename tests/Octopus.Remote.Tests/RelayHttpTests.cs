using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Octopus.Remote.Relay;
using Xunit;

namespace Octopus.Remote.Tests;

public class RelayHttpTests
{
    [Fact]
    public void Custom_roots_validator_accepts_when_no_ssl_policy_errors()
    {
        var validator = RelayHttp.CreateCustomRootsValidator(new X509Certificate2Collection());
        Assert.True(validator(new object(), certificate: null, chain: null, SslPolicyErrors.None));
    }

    [Fact]
    public void Custom_roots_validator_never_bypasses_hostname_or_missing_certificate_errors()
    {
        using var certs = TestCertificateChain.Create("octoterm.si");
        var roots = RelayHttp.LoadPemCertificates(certs.Root.ExportCertificatePem());
        var validator = RelayHttp.CreateCustomRootsValidator(roots);

        using var serverChain = BuildUntrustedChain(certs.Leaf, certs.Intermediate);
        Assert.False(validator(new object(), certs.Leaf, serverChain, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(validator(
            new object(),
            certs.Leaf,
            serverChain,
            SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(validator(new object(), certs.Leaf, serverChain, SslPolicyErrors.RemoteCertificateNotAvailable));
        Assert.False(validator(new object(), certificate: null, serverChain, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Custom_roots_validator_trusts_leaf_and_intermediate_chaining_to_bundled_pem_root()
    {
        using var certs = TestCertificateChain.Create("octoterm.si");
        var roots = RelayHttp.LoadPemCertificates(certs.Root.ExportCertificatePem());
        Assert.Single(roots);

        var validator = RelayHttp.CreateCustomRootsValidator(roots);
        using var serverChain = BuildUntrustedChain(certs.Leaf, certs.Intermediate);

        Assert.True(validator(new object(), certs.Leaf, serverChain, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Custom_roots_validator_rejects_chain_signed_by_untrusted_root()
    {
        using var certs = TestCertificateChain.Create("octoterm.si");
        using var otherRoot = TestCertificateChain.Create("other.example.com");
        var roots = RelayHttp.LoadPemCertificates(otherRoot.Root.ExportCertificatePem());
        var validator = RelayHttp.CreateCustomRootsValidator(roots);

        using var serverChain = BuildUntrustedChain(certs.Leaf, certs.Intermediate);

        Assert.False(validator(new object(), certs.Leaf, serverChain, SslPolicyErrors.RemoteCertificateChainErrors));

        var emptyValidator = RelayHttp.CreateCustomRootsValidator(RelayHttp.LoadPemCertificates("   \n\t  "));
        Assert.False(emptyValidator(new object(), certs.Leaf, serverChain, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void ConfigureWebSocket_applies_callback_only_to_wss_addresses()
    {
        var previous = RelayHttp.WebSocketCertificateValidationCallback;
        try
        {
            var callback = new RemoteCertificateValidationCallback(static (_, _, _, errors) => errors == SslPolicyErrors.None);
            RelayHttp.WebSocketCertificateValidationCallback = callback;

            using var wssSocket = new ClientWebSocket();
            RelayHttp.ConfigureWebSocket(wssSocket.Options, new Uri("wss://octoterm.si/v1/tunnels/abc"));
            Assert.Same(callback, wssSocket.Options.RemoteCertificateValidationCallback);

            using var wsSocket = new ClientWebSocket();
            RelayHttp.ConfigureWebSocket(wsSocket.Options, new Uri("ws://192.168.1.10:8080/octopus/v1"));
            Assert.Null(wsSocket.Options.RemoteCertificateValidationCallback);
        }
        finally
        {
            RelayHttp.WebSocketCertificateValidationCallback = previous;
        }
    }

    [Fact]
    public void Quic_relay_tls_configuration_uses_standard_verification_by_default_without_permissive_override()
    {
        var previous = RelayHttp.WebSocketCertificateValidationCallback;
        try
        {
            RelayHttp.WebSocketCertificateValidationCallback = null;

            var ssl = GetQuicRelaySslOptions(new Uri("quic://relay.example.com:8443"));
            Assert.Equal("relay.example.com", ssl.TargetHost);
            Assert.NotNull(ssl.ApplicationProtocols);
            Assert.Contains(RelaySockets.QuicRelayAlpn, ssl.ApplicationProtocols!);

            if (ssl.RemoteCertificateValidationCallback is { } callback)
            {
                using var certs = TestCertificateChain.Create("relay.example.com");
                using var untrustedChain = BuildUntrustedChain(certs.Leaf, certs.Intermediate);
                Assert.False(callback(new object(), certs.Leaf, untrustedChain, SslPolicyErrors.RemoteCertificateNameMismatch));
                Assert.False(callback(new object(), certs.Leaf, untrustedChain, SslPolicyErrors.RemoteCertificateChainErrors));
                Assert.False(callback(new object(), certs.Leaf, untrustedChain, SslPolicyErrors.RemoteCertificateNotAvailable));
            }

            Assert.Null(ssl.RemoteCertificateValidationCallback);
        }
        finally
        {
            RelayHttp.WebSocketCertificateValidationCallback = previous;
        }
    }

    [Fact]
    public void Quic_and_websocket_relay_tls_share_configured_custom_roots_trust_policy_and_enforce_hostname_and_chain_checks()
    {
        var previous = RelayHttp.WebSocketCertificateValidationCallback;
        try
        {
            using var trusted = TestCertificateChain.Create("relay.example.com");
            using var untrusted = TestCertificateChain.Create("other.example.com");
            var roots = RelayHttp.LoadPemCertificates(trusted.Root.ExportCertificatePem());
            var validator = RelayHttp.CreateCustomRootsValidator(roots);
            RelayHttp.WebSocketCertificateValidationCallback = validator;

            using var wssSocket = new ClientWebSocket();
            RelayHttp.ConfigureWebSocket(wssSocket.Options, new Uri("wss://relay.example.com/v1/tunnel/abc"));
            Assert.Same(validator, wssSocket.Options.RemoteCertificateValidationCallback);

            var quicSsl = GetQuicRelaySslOptions(new Uri("quic://relay.example.com:8443"));
            Assert.Equal("relay.example.com", quicSsl.TargetHost);
            Assert.Same(validator, quicSsl.RemoteCertificateValidationCallback);

            var quicValidator = quicSsl.RemoteCertificateValidationCallback!;
            using var trustedChain = BuildUntrustedChain(trusted.Leaf, trusted.Intermediate);
            using var untrustedChain = BuildUntrustedChain(untrusted.Leaf, untrusted.Intermediate);

            Assert.True(quicValidator(new object(), trusted.Leaf, trustedChain, SslPolicyErrors.None));
            Assert.True(quicValidator(new object(), trusted.Leaf, trustedChain, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(quicValidator(new object(), untrusted.Leaf, untrustedChain, SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(quicValidator(new object(), trusted.Leaf, trustedChain, SslPolicyErrors.RemoteCertificateNameMismatch));
            Assert.False(quicValidator(
                new object(),
                trusted.Leaf,
                trustedChain,
                SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors));
            Assert.False(quicValidator(new object(), trusted.Leaf, trustedChain, SslPolicyErrors.RemoteCertificateNotAvailable));
            Assert.False(quicValidator(new object(), certificate: null, trustedChain, SslPolicyErrors.RemoteCertificateChainErrors));
        }
        finally
        {
            RelayHttp.WebSocketCertificateValidationCallback = previous;
        }
    }

    [Fact]
    public void Clearing_custom_trust_callback_restores_standard_tls_verification_for_both_websocket_and_quic()
    {
        var previous = RelayHttp.WebSocketCertificateValidationCallback;
        try
        {
            RelayHttp.WebSocketCertificateValidationCallback = static (_, _, _, errors) => errors == SslPolicyErrors.None;
            RelayHttp.WebSocketCertificateValidationCallback = null;

            using var wssSocket = new ClientWebSocket();
            RelayHttp.ConfigureWebSocket(wssSocket.Options, new Uri("wss://relay.example.com/v1/host"));
            Assert.Null(wssSocket.Options.RemoteCertificateValidationCallback);

            var quicSsl = GetQuicRelaySslOptions(new Uri("quic://relay.example.com:8443"));
            Assert.Null(quicSsl.RemoteCertificateValidationCallback);
        }
        finally
        {
            RelayHttp.WebSocketCertificateValidationCallback = previous;
        }
    }

    private static SslClientAuthenticationOptions GetQuicRelaySslOptions(Uri quicEndpoint)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            var options = RelaySockets.CreateQuicTunnelOptions(quicEndpoint);
            Assert.Equal(new DnsEndPoint(quicEndpoint.Host, quicEndpoint.Port), options.RemoteEndPoint);
            return options.ClientAuthenticationOptions;
        }

        throw new PlatformNotSupportedException("QUIC relay options require Linux, Windows, or macOS.");
    }

    private static X509Chain BuildUntrustedChain(X509Certificate2 leaf, X509Certificate2 intermediate)
    {
        var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        _ = chain.ChainPolicy.ExtraStore.Add(intermediate);
        var trustedBySystem = chain.Build(leaf);
        Assert.False(trustedBySystem);
        return chain;
    }

    private sealed class TestCertificateChain : IDisposable
    {
        private TestCertificateChain(X509Certificate2 root, X509Certificate2 intermediate, X509Certificate2 leaf)
        {
            Root = root;
            Intermediate = intermediate;
            Leaf = leaf;
        }

        public X509Certificate2 Root { get; }

        public X509Certificate2 Intermediate { get; }

        public X509Certificate2 Leaf { get; }

        public static TestCertificateChain Create(string dnsName)
        {
            var now = DateTimeOffset.UtcNow;

            using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootRequest = new CertificateRequest($"CN=Octopus Test Root ({dnsName})", rootKey, HashAlgorithmName.SHA256);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
            using var rootWithKey = rootRequest.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));

            using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var intermediateRequest = new CertificateRequest($"CN=Octopus Test Intermediate ({dnsName})", intermediateKey, HashAlgorithmName.SHA256);
            intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
            intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            intermediateRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(intermediateRequest.PublicKey, false));
            intermediateRequest.CertificateExtensions.Add(
                X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
                    rootWithKey,
                    includeKeyIdentifier: true,
                    includeIssuerAndSerial: false));
            using var intermediatePublic = intermediateRequest.Create(rootWithKey, now.AddHours(-12), now.AddDays(20), CreateSerialNumber());
            using var intermediateWithKey = intermediatePublic.CopyWithPrivateKey(intermediateKey);

            using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var leafRequest = new CertificateRequest($"CN={dnsName}", leafKey, HashAlgorithmName.SHA256);
            leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                critical: false));
            leafRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(leafRequest.PublicKey, false));
            leafRequest.CertificateExtensions.Add(
                X509AuthorityKeyIdentifierExtension.CreateFromCertificate(
                    intermediatePublic,
                    includeKeyIdentifier: true,
                    includeIssuerAndSerial: false));
            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName(dnsName);
            leafRequest.CertificateExtensions.Add(sanBuilder.Build());
            using var leafPublic = leafRequest.Create(intermediateWithKey, now.AddHours(-6), now.AddDays(10), CreateSerialNumber());

            return new TestCertificateChain(
                X509CertificateLoader.LoadCertificate(rootWithKey.RawData),
                X509CertificateLoader.LoadCertificate(intermediatePublic.RawData),
                X509CertificateLoader.LoadCertificate(leafPublic.RawData));
        }

        public void Dispose()
        {
            Leaf.Dispose();
            Intermediate.Dispose();
            Root.Dispose();
        }

        private static byte[] CreateSerialNumber()
        {
            var serial = RandomNumberGenerator.GetBytes(16);
            serial[0] = (byte)((serial[0] & 0x7F) | 0x01);
            return serial;
        }
    }
}
