using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.WebSockets;

namespace Octopus.Mobile.Voice;

/// <summary>Small pieces the cloud clients share: whole WebSocket messages, gzip, and a WAV header around PCM.</summary>
internal static class CloudWire
{
    /// <summary>One whole message; <see cref="WebSocketMessageType.Close"/> with no data when the other side closed.</summary>
    public static async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return (WebSocketMessageType.Close, []);
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return (result.MessageType, message.ToArray());
        }
    }

    /// <summary>Says goodbye without waiting long for the answer: the text is already in hand.</summary>
    public static async Task CloseQuietlyAsync(WebSocket socket)
    {
        if (socket.State != WebSocketState.Open) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
        }
    }

    public static byte[] Gzip(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(data);
        return output.ToArray();
    }

    public static byte[] Gunzip(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>16-bit PCM in a RIFF/WAVE file, as the HTTP transcription APIs take it.</summary>
    public static byte[] Wav(ReadOnlySpan<byte> pcm16, int sampleRate = 16000, short channels = 1)
    {
        const short bits = 16;
        var wav = new byte[44 + pcm16.Length];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + pcm16.Length);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * channels * bits / 8);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(channels * bits / 8));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], bits);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], pcm16.Length);
        pcm16.CopyTo(span[44..]);
        return wav;
    }
}
