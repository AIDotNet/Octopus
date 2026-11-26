using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Octopus.Mobile.Voice;

/// <summary>
/// Volcengine Doubao streaming recognition, large model (豆包大模型流式语音识别, docs.volcengine.com/docs/6561/1354869):
/// the optimized duplex endpoint, which answers only when the text changes. Binary frames: a full client request
/// with the parameters, then audio-only packets, the last one marked; gzip both ways.
/// </summary>
internal sealed class VolcengineDictationEngine(Func<IAudioCapture> audio) : IDictationEngine
{
    public DictationProblem Check(VoiceSettings settings) =>
        string.IsNullOrWhiteSpace(settings.VolcengineAccessToken) ? DictationProblem.NotConfigured : DictationProblem.None;

    public async Task<IDictationSession> StartAsync(VoiceSettings settings, CancellationToken cancellationToken)
    {
        var session = new VolcengineDictationSession(settings, audio());
        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return session;
    }
}

internal sealed class VolcengineDictationSession(VoiceSettings settings, IAudioCapture capture) : CloudDictationSession(settings, capture)
{
    private static readonly Uri Endpoint = new("wss://openspeech.bytedance.com/api/v3/sauc/bigmodel_async");

    protected override async Task<string> RunAsync(ChannelReader<byte[]> audio, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        foreach (var (name, value) in VolcengineProtocol.HeadersOf(Settings, Guid.NewGuid().ToString()))
        {
            socket.Options.SetRequestHeader(name, value);
        }
        await ConnectAsync(socket, Endpoint, cancellationToken).ConfigureAwait(false);

        await socket.SendAsync(VolcengineProtocol.FullRequest(Settings), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        var receiving = ReceiveAsync(socket, cancellationToken);

        // One chunk held back, so the last one can go out marked as the last.
        byte[]? held = null;
        await foreach (var chunk in audio.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (receiving.IsCompleted) break;
            if (held is not null)
            {
                await socket.SendAsync(VolcengineProtocol.Audio(held, last: false), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
            held = chunk;
        }
        if (!receiving.IsCompleted)
        {
            await socket.SendAsync(VolcengineProtocol.Audio(held ?? [], last: true), WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        var text = await receiving.ConfigureAwait(false);
        await CloudWire.CloseQuietlyAsync(socket).ConfigureAwait(false);
        return text;
    }

    private async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var text = "";
        while (true)
        {
            var (type, data) = await CloudWire.ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
            if (type == WebSocketMessageType.Close) return text;
            if (type != WebSocketMessageType.Binary) continue;
            var frame = VolcengineProtocol.Parse(data);
            if (frame.ErrorCode is { } code)
            {
                throw new DictationException(VolcengineProtocol.KindOf(code), $"{code}: {frame.ErrorMessage}");
            }
            if (frame.Result is { } result)
            {
                text = result.Text;
                Report(result.Settled, result.Tail);
            }
            if (frame.IsLast) return text;
        }
    }
}

/// <summary>What one server response says: the whole text so far, split into the settled utterances and the rest.</summary>
internal readonly record struct VolcengineResult(string Text, string Settled, string Tail);

/// <summary>One server frame: a result, or an error with its code.</summary>
internal readonly record struct VolcengineFrame(VolcengineResult? Result, bool IsLast, uint? ErrorCode, string? ErrorMessage);

/// <summary>The binary protocol of the v3 streaming recognition (big-endian integers, 4-byte header).</summary>
internal static class VolcengineProtocol
{
    private const byte VersionAndHeaderSize = 0x11; // protocol version 1, header of one 4-byte word
    private const byte FullClientRequest = 0x1;
    private const byte AudioOnlyRequest = 0x2;
    private const byte FullServerResponse = 0x9;
    private const byte ServerError = 0xF;
    private const byte FlagPositiveSequence = 0x1;
    private const byte FlagLastNoSequence = 0x2;
    private const byte FlagLastNegativeSequence = 0x3;
    private const byte SerializationNone = 0x0;
    private const byte SerializationJson = 0x1;
    private const byte CompressionGzip = 0x1;

    /// <summary>
    /// The handshake headers. The newer console has one API key (X-Api-Key); the older one an APP ID and an Access
    /// Token. The resource id goes along either way.
    /// </summary>
    public static IEnumerable<(string Name, string Value)> HeadersOf(VoiceSettings settings, string connectId)
    {
        var token = settings.VolcengineAccessToken.Trim();
        if (settings.VolcengineAppId.Trim() is { Length: > 0 } appId)
        {
            yield return ("X-Api-App-Key", appId);
            yield return ("X-Api-Access-Key", token);
        }
        else
        {
            yield return ("X-Api-Key", token);
        }
        yield return ("X-Api-Resource-Id", string.IsNullOrWhiteSpace(settings.VolcengineResourceId) ? VoiceSettings.DefaultVolcengineResourceId : settings.VolcengineResourceId.Trim());
        yield return ("X-Api-Connect-Id", connectId);
        yield return ("X-Api-Request-Id", connectId);
        yield return ("X-Api-Sequence", "-1");
    }

    public static byte[] FullRequest(VoiceSettings settings)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartObject("user");
            json.WriteString("uid", "octopus-mobile");
            json.WriteEndObject();
            json.WriteStartObject("audio");
            json.WriteString("format", "pcm");
            json.WriteString("codec", "raw");
            json.WriteNumber("rate", 16000);
            json.WriteNumber("bits", 16);
            json.WriteNumber("channel", 1);
            json.WriteEndObject();
            json.WriteStartObject("request");
            json.WriteString("model_name", "bigmodel");
            json.WriteBoolean("enable_itn", true);
            json.WriteBoolean("enable_punc", settings.Punctuation);
            json.WriteBoolean("show_utterances", true);
            json.WriteString("result_type", "full");
            if (settings.Hotwords.Count > 0)
            {
                // Inline hotwords: a JSON string inside the request, about 100 tokens on the streaming endpoints.
                json.WriteStartObject("corpus");
                json.WriteString("context", HotwordContext(settings.Hotwords));
                json.WriteEndObject();
            }
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Frame(FullClientRequest, 0, SerializationJson, buffer.ToArray());
    }

    public static string HotwordContext(IReadOnlyList<string> hotwords)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartArray("hotwords");
            foreach (var word in hotwords.Take(30))
            {
                json.WriteStartObject();
                json.WriteString("word", word);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>An audio-only packet; the last one carries the "last packet" flag (no sequence number).</summary>
    public static byte[] Audio(ReadOnlySpan<byte> pcm, bool last) =>
        Frame(AudioOnlyRequest, last ? FlagLastNoSequence : (byte)0, SerializationNone, pcm);

    public static VolcengineFrame Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4) throw new IOException("Volcengine sent a frame without a header.");
        var headerSize = (frame[0] & 0x0F) * 4;
        var type = (byte)(frame[1] >> 4);
        var flags = (byte)(frame[1] & 0x0F);
        var compressed = (frame[2] & 0x0F) == CompressionGzip;
        var offset = headerSize;

        if (type == ServerError)
        {
            var code = BinaryPrimitives.ReadUInt32BigEndian(frame[offset..]);
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(frame[(offset + 4)..]);
            var message = frame.Slice(offset + 8, Math.Min(length, frame.Length - offset - 8));
            return new VolcengineFrame(null, true, code, Encoding.UTF8.GetString(compressed ? CloudWire.Gunzip(message) : message));
        }
        if (type != FullServerResponse) return new VolcengineFrame(null, false, null, null);

        if (flags is FlagPositiveSequence or FlagLastNegativeSequence) offset += 4;
        var isLast = flags is FlagLastNoSequence or FlagLastNegativeSequence;
        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(frame[offset..]);
        var payload = frame.Slice(offset + 4, Math.Min(size, frame.Length - offset - 4));
        var json = compressed ? CloudWire.Gunzip(payload) : payload.ToArray();
        return new VolcengineFrame(json.Length == 0 ? null : ResultOf(json), isLast, null, null);
    }

    /// <summary>45000002 is "empty audio": nothing was said.</summary>
    public static DictationErrorKind KindOf(uint code) => code == 45000002 ? DictationErrorKind.NoSpeech : DictationErrorKind.Provider;

    private static VolcengineResult? ResultOf(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return null;
        var text = result.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var settled = new StringBuilder();
        if (result.TryGetProperty("utterances", out var utterances) && utterances.ValueKind == JsonValueKind.Array)
        {
            foreach (var utterance in utterances.EnumerateArray())
            {
                if (!utterance.TryGetProperty("definite", out var definite) || definite.ValueKind != JsonValueKind.True) break;
                settled.Append(utterance.TryGetProperty("text", out var u) ? u.GetString() : "");
            }
        }
        var done = settled.ToString();
        return text.StartsWith(done, StringComparison.Ordinal)
            ? new VolcengineResult(text, done, text[done.Length..])
            : new VolcengineResult(text, "", text);
    }

    private static byte[] Frame(byte type, byte flags, byte serialization, ReadOnlySpan<byte> payload)
    {
        var body = CloudWire.Gzip(payload);
        var frame = new byte[8 + body.Length];
        frame[0] = VersionAndHeaderSize;
        frame[1] = (byte)((type << 4) | flags);
        frame[2] = (byte)((serialization << 4) | CompressionGzip);
        frame[3] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)body.Length);
        body.CopyTo(frame, 8);
        return frame;
    }
}
