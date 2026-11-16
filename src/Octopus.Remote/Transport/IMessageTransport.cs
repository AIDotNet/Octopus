using System.Threading.Channels;

namespace Octopus.Remote.Transport;

/// <summary>
/// A reliable, ordered pipe of whole binary messages between two devices: a WebSocket on the local network, or two
/// WebSockets joined by the relay. Nothing about it is trusted; the secure channel on top gives it secrecy and
/// tells who is at the other end.
/// </summary>
public interface IMessageTransport : IAsyncDisposable
{
    /// <summary>Sends one message. One sender at a time.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default);

    /// <summary>The next message, or null when the other side closed. One receiver at a time. A message larger than <paramref name="maxLength"/> ends the transport with <see cref="IOException"/>.</summary>
    ValueTask<byte[]?> ReceiveAsync(int maxLength, CancellationToken cancellationToken = default);
}

/// <summary>Two ends in one process, for tests and for a view of this computer's own sessions.</summary>
public sealed class InMemoryTransport : IMessageTransport
{
    private readonly ChannelReader<byte[]> _incoming;
    private readonly ChannelWriter<byte[]> _outgoing;

    private InMemoryTransport(ChannelReader<byte[]> incoming, ChannelWriter<byte[]> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    /// <summary>A connected pair: what one sends, the other receives.</summary>
    public static (InMemoryTransport A, InMemoryTransport B) CreatePair()
    {
        var aToB = Channel.CreateUnbounded<byte[]>();
        var bToA = Channel.CreateUnbounded<byte[]>();
        return (new InMemoryTransport(bToA.Reader, aToB.Writer), new InMemoryTransport(aToB.Reader, bToA.Writer));
    }

    /// <summary>Changes each message on its way out; for tests of what a tampering network does.</summary>
    public Func<byte[], byte[]?>? Outgoing { get; set; }

    public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
    {
        var bytes = message.ToArray();
        if (Outgoing is { } change)
        {
            if (change(bytes) is not { } changed) return ValueTask.CompletedTask; // dropped
            bytes = changed;
        }
        if (!_outgoing.TryWrite(bytes)) throw new IOException("The transport is closed.");
        return ValueTask.CompletedTask;
    }

    public async ValueTask<byte[]?> ReceiveAsync(int maxLength, CancellationToken cancellationToken = default)
    {
        try
        {
            var message = await _incoming.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (message.Length > maxLength) throw new IOException($"A message of {message.Length} bytes exceeds the limit of {maxLength}.");
            return message;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _outgoing.TryComplete();
        return ValueTask.CompletedTask;
    }
}
