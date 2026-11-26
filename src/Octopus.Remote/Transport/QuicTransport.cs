using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

namespace Octopus.Remote.Transport;

/// <summary>
/// Transport provider for <c>quic://host:port</c> using <see cref="System.Net.Quic"/> and <see cref="FramedStreamTransport"/>
/// (design/remote/传输协议v2与多传输优化.md §6.2). TLS uses an ephemeral self-signed P-256 certificate while device
/// identity authentication and end-to-end record encryption are enforced by <see cref="Security.SecureChannel"/>.
/// </summary>
public sealed class QuicTransportProvider : ITransportProvider
{
    public const string SchemeName = "quic";
    public static readonly SslApplicationProtocol ApplicationProtocol = new("octopus-v2");

    public string Scheme => SchemeName;

    public bool IsSupported => QuicMessageTransport.IsSupported;

    public Task<IMessageTransport> ConnectAsync(Uri address, CancellationToken cancellationToken = default) =>
        QuicMessageTransport.ConnectAsync(address, cancellationToken);
}

/// <summary>Client-side QUIC transport over a bidirectional QUIC stream.</summary>
public static class QuicMessageTransport
{
    public static bool IsSupported =>
        (OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        && QuicConnectionSupported();

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("macos")]
    private static bool QuicConnectionSupported() => QuicConnection.IsSupported;

    public static async Task<IMessageTransport> ConnectAsync(Uri address, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return await ConnectCoreAsync(address, cancellationToken).ConfigureAwait(false);
        }
        throw new PlatformNotSupportedException("QUIC transport is only supported on desktop platforms with libmsquic.");
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("macos")]
    private static async Task<IMessageTransport> ConnectCoreAsync(Uri address, CancellationToken cancellationToken)
    {
        if (!QuicConnection.IsSupported)
        {
            throw new PlatformNotSupportedException("QUIC is not available on this machine (libmsquic is missing).");
        }
        var port = address.IsDefaultPort || address.Port <= 0 ? 47601 : address.Port;
        DnsEndPoint endPoint = new(address.Host, port);
        var options = new QuicClientConnectionOptions
        {
            RemoteEndPoint = endPoint,
            DefaultStreamErrorCode = 0x0A,
            DefaultCloseErrorCode = 0x0B,
            MaxInboundBidirectionalStreams = 4,
            MaxInboundUnidirectionalStreams = 1,
            IdleTimeout = TimeSpan.FromSeconds(60),
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [QuicTransportProvider.ApplicationProtocol],
                TargetHost = address.Host,
                // SecureChannel performs mutual ECDSA device-key authentication on top of the transport.
#pragma warning disable CA5359 // QUIC 只作传输层；SecureChannel 在其上做双向 ECDSA 设备密钥认证
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
#pragma warning restore CA5359
            },
        };

        var connection = await QuicConnection.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken).ConfigureAwait(false);
            return new FramedStreamTransport(stream, asyncOwner: connection);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>Host-side QUIC listener accepting bidirectional QUIC streams as <see cref="IMessageTransport"/> instances.</summary>
public sealed class QuicTransportListener : IAsyncDisposable
{
    private readonly IPAddress _address;
    private readonly int _requestedPort;
    private readonly Channel<AcceptedTransport> _ready = Channel.CreateBounded<AcceptedTransport>(16);
    private readonly CancellationTokenSource _stop = new();
    private QuicListener? _listener;
    private X509Certificate2? _certificate;
    private Task? _loop;

    public QuicTransportListener(IPAddress address, int port)
    {
        _address = address;
        _requestedPort = port;
    }

    public static bool IsSupported =>
        (OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        && QuicListenerSupported();

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("macos")]
    private static bool QuicListenerSupported() => QuicListener.IsSupported;

    public Func<IPAddress, bool> AllowRemote { get; init; } = LocalNetwork.IsLocal;

    public int Port { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        throw new PlatformNotSupportedException("QUIC listener is not supported on this platform.");
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("macos")]
    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (!QuicListener.IsSupported)
        {
            throw new PlatformNotSupportedException("QUIC is not available on this machine (libmsquic is missing).");
        }
        _certificate = CreateEphemeralCertificate();
        var serverOptions = new QuicServerConnectionOptions
        {
            DefaultStreamErrorCode = 0x0A,
            DefaultCloseErrorCode = 0x0B,
            MaxInboundBidirectionalStreams = 4,
            MaxInboundUnidirectionalStreams = 1,
            IdleTimeout = TimeSpan.FromSeconds(60),
            ServerAuthenticationOptions = new SslServerAuthenticationOptions
            {
                ApplicationProtocols = [QuicTransportProvider.ApplicationProtocol],
                ServerCertificate = _certificate,
                ClientCertificateRequired = false,
            },
        };
        _listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(_address, _requestedPort),
            ApplicationProtocols = [QuicTransportProvider.ApplicationProtocol],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(serverOptions),
        }, cancellationToken).ConfigureAwait(false);
        Port = _listener.LocalEndPoint.Port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public async Task<AcceptedTransport> AcceptAsync(CancellationToken cancellationToken = default) =>
        await _ready.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("macos")]
    private async Task AcceptLoopAsync()
    {
        var listener = _listener;
        if (listener is null) return;
        while (!_stop.IsCancellationRequested)
        {
            QuicConnection connection;
            try
            {
                connection = await listener.AcceptConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or QuicException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => HandleConnectionAsync(connection));
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("macos")]
    private async Task HandleConnectionAsync(QuicConnection connection)
    {
        try
        {
            var remote = connection.RemoteEndPoint;
            if (!AllowRemote(remote.Address))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return;
            }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var stream = await connection.AcceptInboundStreamAsync(deadline.Token).ConfigureAwait(false);
            var accepted = new AcceptedTransport(new FramedStreamTransport(stream, asyncOwner: connection), remote);
            if (!_ready.Writer.TryWrite(accepted))
            {
                await accepted.Transport.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            try { await connection.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    private static X509Certificate2 CreateEphemeralCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=octopus-quic", key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // Exporting and re-importing as PFX ensures the private key is accessible to SslStream/MsQuic on Windows as well as Unix.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if ((OperatingSystem.IsLinux() || OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) && _listener is not null)
        {
            await DisposeListenerCoreAsync().ConfigureAwait(false);
        }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
        _ready.Writer.TryComplete();
        while (_ready.Reader.TryRead(out var pending))
        {
            await pending.Transport.DisposeAsync().ConfigureAwait(false);
        }
        _certificate?.Dispose();
        _stop.Dispose();
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("macos")]
    private async ValueTask DisposeListenerCoreAsync()
    {
        if (_listener is { } listener)
        {
            _listener = null;
            try { await listener.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }
}
