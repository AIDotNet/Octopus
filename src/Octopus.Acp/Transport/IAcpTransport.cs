using System.Text;
using System.Text.Json;

namespace Octopus.Acp.Transport;

/// <summary>
/// Bidirectional JSON-RPC 2.0 message transport for ACP (local <c>stdio</c>, remote <c>websocket</c>, or <c>streamable-http</c>).
/// </summary>
public interface IAcpTransport : IAsyncDisposable
{
    /// <summary>Maximum allowed byte size of a single outgoing or incoming JSON-RPC payload.</summary>
    int MaxLineBytes { get; }

    /// <summary>Sends a serialized JSON-RPC 2.0 message frame.</summary>
    Task SendAsync(string jsonLine, CancellationToken cancellationToken);

    /// <summary>
    /// Continuously reads incoming JSON-RPC messages until EOF, closure, or <paramref name="cancellationToken"/> cancellation.
    /// Each <see cref="JsonDocument"/> passed to <paramref name="onMessage"/> is owned and disposed by the receiver.
    /// </summary>
    Task RunReceiveLoopAsync(
        Action<JsonDocument> onMessage,
        Action<string> onOtherLine,
        Action<OversizedAcpLine> onOversized,
        CancellationToken cancellationToken);
}

/// <summary>NDJSON-over-stream transport used for local ACP child processes over <c>stdio</c>.</summary>
public sealed class AcpStreamTransport : IAcpTransport
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly NdjsonLineFramer _framer;

    public AcpStreamTransport(Stream input, Stream output, int maxLineBytes = NdjsonLineFramer.DefaultMaxLineBytes)
    {
        _input = input;
        _output = output;
        _framer = new NdjsonLineFramer(maxLineBytes);
    }

    public int MaxLineBytes => _framer.MaxLineBytes;

    public async Task SendAsync(string jsonLine, CancellationToken cancellationToken)
    {
        var bytes = Utf8NoBom.GetBytes(jsonLine + "\n");
        await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RunReceiveLoopAsync(
        Action<JsonDocument> onMessage,
        Action<string> onOtherLine,
        Action<OversizedAcpLine> onOversized,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await _input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            _framer.Feed(buffer.AsSpan(0, read), onMessage, onOtherLine, onOversized);
        }
        _framer.Complete(onMessage, onOtherLine, onOversized);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
