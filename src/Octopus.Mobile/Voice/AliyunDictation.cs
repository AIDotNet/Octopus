using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Octopus.Mobile.Voice;

/// <summary>
/// Alibaba Cloud Model Studio (百炼) real-time recognition, Fun-ASR by default: one WebSocket task per dictation
/// (help.aliyun.com/zh/model-studio/fun-asr-realtime-websocket-api). run-task, wait for task-started, PCM as binary
/// frames, finish-task, and the results until task-finished.
/// </summary>
internal sealed class AliyunDictationEngine(Func<IAudioCapture> audio) : IDictationEngine
{
    public DictationProblem Check(VoiceSettings settings) =>
        string.IsNullOrWhiteSpace(settings.AliyunApiKey) ? DictationProblem.NotConfigured : DictationProblem.None;

    public async Task<IDictationSession> StartAsync(VoiceSettings settings, CancellationToken cancellationToken)
    {
        var session = new AliyunDictationSession(settings, audio());
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

internal sealed class AliyunDictationSession(VoiceSettings settings, IAudioCapture capture) : CloudDictationSession(settings, capture)
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);

    protected override async Task<string> RunAsync(ChannelReader<byte[]> audio, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + Settings.AliyunApiKey.Trim());
        await ConnectAsync(socket, AliyunProtocol.EndpointOf(Settings), cancellationToken).ConfigureAwait(false);

        var taskId = Guid.NewGuid().ToString("N");
        var transcript = new AliyunTranscript();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendTextAsync(socket, AliyunProtocol.RunTask(taskId, Settings), cancellationToken).ConfigureAwait(false);
        var receiving = ReceiveAsync(socket, transcript, started, cancellationToken);

        // No audio may go before task-started.
        await Task.WhenAny(started.Task, receiving).WaitAsync(StartTimeout, cancellationToken).ConfigureAwait(false);
        if (receiving.IsCompleted) return await receiving.ConfigureAwait(false);

        await foreach (var chunk in audio.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await socket.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        await SendTextAsync(socket, AliyunProtocol.FinishTask(taskId), cancellationToken).ConfigureAwait(false);
        var text = await receiving.ConfigureAwait(false);
        await CloudWire.CloseQuietlyAsync(socket).ConfigureAwait(false);
        return text;
    }

    private async Task<string> ReceiveAsync(ClientWebSocket socket, AliyunTranscript transcript, TaskCompletionSource started, CancellationToken cancellationToken)
    {
        while (true)
        {
            var (type, data) = await CloudWire.ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
            if (type == WebSocketMessageType.Close) throw new IOException("Alibaba Cloud closed the connection.");
            if (type != WebSocketMessageType.Text) continue;
            var message = AliyunProtocol.Parse(data);
            switch (message.Event)
            {
                case "task-started":
                    started.TrySetResult();
                    break;
                case "result-generated":
                    if (transcript.Apply(message)) Report(transcript.Settled, transcript.Tail);
                    break;
                case "task-finished":
                    return transcript.Text;
                case "task-failed":
                    throw new DictationException(AliyunProtocol.KindOf(message.ErrorCode), $"{message.ErrorCode}: {message.ErrorMessage}");
            }
        }
    }

    private static Task SendTextAsync(ClientWebSocket socket, string json, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
}

/// <summary>One server event of a Fun-ASR task, with what the client needs of it.</summary>
internal readonly record struct AliyunEvent(
    string Event,
    string? ErrorCode,
    string? ErrorMessage,
    int SentenceId,
    string Text,
    bool SentenceEnd,
    bool Heartbeat);

/// <summary>The messages of the Fun-ASR WebSocket API, written and read without reflection (NativeAOT).</summary>
internal static class AliyunProtocol
{
    public static Uri EndpointOf(VoiceSettings settings)
    {
        var international = settings.AliyunRegion == AliyunRegion.International;
        var workspace = settings.AliyunWorkspaceId.Trim();
        return new Uri(workspace.Length > 0
            ? $"wss://{workspace}.{(international ? "ap-southeast-1" : "cn-beijing")}.maas.aliyuncs.com/api-ws/v1/inference"
            : $"wss://{(international ? "dashscope-intl" : "dashscope")}.aliyuncs.com/api-ws/v1/inference");
    }

    public static string RunTask(string taskId, VoiceSettings settings)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            WriteHeader(json, "run-task", taskId);
            json.WriteStartObject("payload");
            json.WriteString("task_group", "audio");
            json.WriteString("task", "asr");
            json.WriteString("function", "recognition");
            json.WriteString("model", Model(settings));
            json.WriteStartObject("parameters");
            json.WriteString("format", "pcm");
            json.WriteNumber("sample_rate", 16000);
            if (settings.LanguageCode is { } language)
            {
                json.WriteStartArray("language_hints");
                json.WriteStringValue(language);
                json.WriteEndArray();
            }
            // Paraformer has a switch for punctuation; Fun-ASR always punctuates.
            if (settings.AliyunModel.StartsWith("paraformer", StringComparison.OrdinalIgnoreCase))
            {
                json.WriteBoolean("punctuation_prediction_enabled", settings.Punctuation);
            }
            json.WriteEndObject();
            json.WriteStartObject("input");
            // Fun-ASR takes context text that biases it toward the words in it (400 characters at most); Paraformer has none.
            if (settings.Hotwords.Count > 0 && Model(settings).StartsWith("fun-asr", StringComparison.OrdinalIgnoreCase))
            {
                json.WriteStartArray("context");
                json.WriteStartObject();
                json.WriteString("role", "user");
                json.WriteStartArray("content");
                json.WriteStartObject();
                json.WriteString("type", "input_text");
                json.WriteString("text", VoiceHotwords.Joined(settings.Hotwords, 380));
                json.WriteEndObject();
                json.WriteEndArray();
                json.WriteEndObject();
                json.WriteEndArray();
            }
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Model(VoiceSettings settings) =>
        string.IsNullOrWhiteSpace(settings.AliyunModel) ? VoiceSettings.DefaultAliyunModel : settings.AliyunModel.Trim();

    public static string FinishTask(string taskId)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            WriteHeader(json, "finish-task", taskId);
            json.WriteStartObject("payload");
            json.WriteStartObject("input");
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static AliyunEvent Parse(ReadOnlySpan<byte> utf8)
    {
        using var document = JsonDocument.Parse(utf8.ToArray());
        var root = document.RootElement;
        var header = root.TryGetProperty("header", out var h) ? h : default;
        var name = header.ValueKind == JsonValueKind.Object && header.TryGetProperty("event", out var e) ? e.GetString() ?? "" : "";
        var errorCode = header.ValueKind == JsonValueKind.Object && header.TryGetProperty("error_code", out var c) ? c.GetString() : null;
        var errorMessage = header.ValueKind == JsonValueKind.Object && header.TryGetProperty("error_message", out var m) ? m.GetString() : null;
        if (root.TryGetProperty("payload", out var payload)
            && payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty("sentence", out var sentence)
            && sentence.ValueKind == JsonValueKind.Object)
        {
            return new AliyunEvent(
                name,
                errorCode,
                errorMessage,
                sentence.TryGetProperty("sentence_id", out var id) && id.TryGetInt32(out var number) ? number : 0,
                sentence.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "",
                sentence.TryGetProperty("sentence_end", out var end) && end.ValueKind == JsonValueKind.True,
                sentence.TryGetProperty("heartbeat", out var beat) && beat.ValueKind == JsonValueKind.True);
        }
        return new AliyunEvent(name, errorCode, errorMessage, 0, "", false, false);
    }

    public static DictationErrorKind KindOf(string? errorCode) => errorCode switch
    {
        null => DictationErrorKind.Provider,
        _ when errorCode.Contains("Auth", StringComparison.OrdinalIgnoreCase)
            || errorCode.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
            || errorCode.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) => DictationErrorKind.Auth,
        _ => DictationErrorKind.Provider,
    };

    private static void WriteHeader(Utf8JsonWriter json, string action, string taskId)
    {
        json.WriteStartObject("header");
        json.WriteString("action", action);
        json.WriteString("task_id", taskId);
        json.WriteString("streaming", "duplex");
        json.WriteEndObject();
    }
}

/// <summary>
/// The sentences so far. Each result is the whole sentence up to now (not a delta): it replaces the sentence with the
/// same id, which is settled once <c>sentence_end</c> comes.
/// </summary>
internal sealed class AliyunTranscript
{
    private readonly SortedDictionary<int, string> _settled = [];
    private int _currentId;
    private string _current = "";

    public string Settled => string.Concat(_settled.Values);
    public string Tail => _current;
    public string Text => Settled + _current;

    /// <summary>Takes one result-generated event; false when it changed nothing (a heartbeat).</summary>
    public bool Apply(AliyunEvent message)
    {
        if (message.Heartbeat || message.SentenceId == 0) return false;
        if (message.SentenceEnd)
        {
            _settled[message.SentenceId] = message.Text;
            if (_currentId == message.SentenceId)
            {
                _current = "";
                _currentId = 0;
            }
            return true;
        }
        _currentId = message.SentenceId;
        _current = message.Text;
        return true;
    }
}
