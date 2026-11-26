using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Octopus.Remote.Transport;

/// <summary>
/// A pluggable transport driver that can open an <see cref="IMessageTransport"/> for a URI scheme
/// (design/remote/传输协议v2与多传输优化.md §6.1).
/// </summary>
public interface ITransportProvider
{
    /// <summary>The URI scheme this provider handles (e.g. <c>ws</c>, <c>wss</c>, <c>quic</c>, <c>octopus-p2p</c>).</summary>
    string Scheme { get; }

    /// <summary>True when the current OS and runtime support this transport.</summary>
    bool IsSupported { get; }

    /// <summary>Opens a message transport to <paramref name="address"/>.</summary>
    Task<IMessageTransport> ConnectAsync(Uri address, CancellationToken cancellationToken = default);
}

/// <summary>
/// Registry of transport providers by URI scheme. Pre-registers WebSocket (<c>ws</c>, <c>wss</c>) and QUIC (<c>quic</c>),
/// and allows custom or P2P providers to be registered at run time.
/// </summary>
public sealed class TransportRegistry
{
    private readonly ConcurrentDictionary<string, ITransportProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public static TransportRegistry Default { get; } = CreateDefault();

    public static TransportRegistry CreateDefault()
    {
        var registry = new TransportRegistry();
        registry.Register(new WebSocketTransportProvider("ws"));
        registry.Register(new WebSocketTransportProvider("wss"));
        registry.Register(new QuicTransportProvider());
        return registry;
    }

    public void Register(ITransportProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[provider.Scheme] = provider;
    }

    public ITransportProvider? Find(string scheme) =>
        _providers.TryGetValue(scheme, out var provider) ? provider : null;

    public bool IsSupported(string scheme) =>
        _providers.TryGetValue(scheme, out var provider) && provider.IsSupported;

    public IReadOnlyList<string> SupportedSchemes =>
        [.. _providers.Values.Where(p => p.IsSupported).Select(p => p.Scheme)];

    public Task<IMessageTransport> ConnectAsync(Uri address, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!_providers.TryGetValue(address.Scheme, out var provider))
        {
            throw new NotSupportedException($"No transport provider is registered for scheme '{address.Scheme}'.");
        }
        if (!provider.IsSupported)
        {
            throw new NotSupportedException($"Transport scheme '{address.Scheme}' is not supported on this system.");
        }
        return provider.ConnectAsync(address, cancellationToken);
    }

    private sealed class WebSocketTransportProvider(string scheme) : ITransportProvider
    {
        public string Scheme { get; } = scheme;
        public bool IsSupported => true;
        public async Task<IMessageTransport> ConnectAsync(Uri address, CancellationToken cancellationToken = default) =>
            await WebSocketTransport.ConnectAsync(address, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Turns any full-duplex byte <see cref="Stream"/> (such as a QUIC bidirectional stream, Unix domain socket, named pipe,
/// or SSH stdio pipe) into an <see cref="IMessageTransport"/> using 4-byte little-endian length-prefixed framing.
/// </summary>
public sealed class FramedStreamTransport(Stream stream, IAsyncDisposable? asyncOwner = null, IDisposable? owner = null) : IMessageTransport
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    public async ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0) throw new IOException("The transport is closed.");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(4 + message.Length);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), message.Length);
            message.Span.CopyTo(buffer.AsSpan(4));
            await stream.WriteAsync(buffer.AsMemory(0, 4 + message.Length), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException and not IOException)
        {
            throw new IOException("The connection is closed.", e);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            _writeGate.Release();
        }
    }

    public async ValueTask<byte[]?> ReceiveAsync(int maxLength, CancellationToken cancellationToken = default)
    {
        var header = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            if (!await ReadExactOrEofAsync(stream, header.AsMemory(0, 4), cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
            if (length < 0 || length > maxLength)
            {
                throw new IOException($"A framed message of {length} bytes exceeds the limit of {maxLength}.");
            }
            if (length == 0) return [];
            var payload = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await stream.ReadAsync(payload.AsMemory(offset, length - offset), cancellationToken).ConfigureAwait(false);
                if (read == 0) return null;
                offset += read;
            }
            return payload;
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    private static async ValueTask<bool> ReadExactOrEofAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            int read;
            try
            {
                read = await source.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                return false;
            }
            if (read == 0)
            {
                if (offset == 0) return false;
                throw new IOException("The stream ended in the middle of a frame header.");
            }
            offset += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await stream.DisposeAsync().ConfigureAwait(false); } catch { }
        if (asyncOwner is not null)
        {
            try { await asyncOwner.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        try { owner?.Dispose(); } catch { }
    }
}
