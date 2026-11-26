using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Octopus.Remote.Relay;

namespace Octopus.Mobile.Voice;

/// <summary>
/// OpenAI speech to text (developers.openai.com/api/docs/guides/speech-to-text), or any service with the same
/// <c>/audio/transcriptions</c>: the audio is kept while the finger is down, then sent once as WAV with
/// <c>stream=true</c>, and the text streams back. No live text while speaking; the realtime API would give that at
/// several times the price, and needs 24 kHz audio.
/// </summary>
internal sealed class OpenAiDictationEngine(Func<IAudioCapture> audio, Func<HttpMessageHandler>? http) : IDictationEngine
{
    public DictationProblem Check(VoiceSettings settings) =>
        string.IsNullOrWhiteSpace(settings.OpenAiApiKey) ? DictationProblem.NotConfigured : DictationProblem.None;

    public async Task<IDictationSession> StartAsync(VoiceSettings settings, CancellationToken cancellationToken)
    {
        var session = new OpenAiDictationSession(settings, audio(), http ?? (() => RelayHttp.CreateHandler()));
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

internal sealed class OpenAiDictationSession(VoiceSettings settings, IAudioCapture capture, Func<HttpMessageHandler> http)
    : CloudDictationSession(settings, capture)
{
    /// <summary>Less than this much audio is a tap, not speech: nothing is sent.</summary>
    private const int MinBytes = 16000 * 2 * 3 / 10;

    protected override async Task<string> RunAsync(ChannelReader<byte[]> audio, CancellationToken cancellationToken)
    {
        using var pcm = new MemoryStream();
        await foreach (var chunk in audio.ReadAllAsync(cancellationToken).ConfigureAwait(false)) pcm.Write(chunk);
        if (pcm.Length < MinBytes) return "";

        using var client = new HttpClient(http(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiProtocol.EndpointOf(Settings))
        {
            Content = OpenAiProtocol.Form(Settings, CloudWire.Wav(pcm.GetBuffer().AsSpan(0, (int)pcm.Length))),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Settings.OpenAiApiKey.Trim());
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new DictationException(KindOf(response.StatusCode), OpenAiProtocol.ErrorOf(body) ?? $"HTTP {(int)response.StatusCode}");
        }

        // A compatible service may ignore stream=true and answer with plain JSON.
        if (response.Content.Headers.ContentType?.MediaType is not "text/event-stream")
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return OpenAiProtocol.TextOf(body);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            switch (OpenAiProtocol.ParseEvent(line))
            {
                case { Type: "transcript.text.delta", Text: var delta }:
                    text.Append(delta);
                    Report("", text.ToString());
                    break;
                case { Type: "transcript.text.done", Text: var done }:
                    return done;
            }
        }
        return text.ToString();
    }
}

/// <summary>One server-sent event of a streamed transcription.</summary>
internal readonly record struct OpenAiEvent(string Type, string Text);

internal static class OpenAiProtocol
{
    public static Uri EndpointOf(VoiceSettings settings)
    {
        var baseUrl = string.IsNullOrWhiteSpace(settings.OpenAiBaseUrl) ? VoiceSettings.DefaultOpenAiBaseUrl : settings.OpenAiBaseUrl.Trim();
        return new Uri(baseUrl.TrimEnd('/') + "/audio/transcriptions");
    }

    public static MultipartFormDataContent Form(VoiceSettings settings, byte[] wav)
    {
        var model = string.IsNullOrWhiteSpace(settings.OpenAiModel) ? VoiceSettings.DefaultOpenAiModel : settings.OpenAiModel.Trim();
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "speech.wav");
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("true"), "stream");
        // gpt-transcribe takes a list of languages and keywords; the older models one language and a prompt.
        var current = model.StartsWith("gpt-transcribe", StringComparison.OrdinalIgnoreCase);
        if (settings.LanguageCode is { } language) form.Add(new StringContent(language), current ? "languages[]" : "language");
        if (settings.Hotwords.Count > 0)
        {
            if (current)
            {
                // A keyword with < > or a line break makes the whole request fail.
                foreach (var word in settings.Hotwords.Where(word => word.IndexOfAny(['<', '>', '\r', '\n']) < 0).Take(30))
                {
                    form.Add(new StringContent(word), "keywords[]");
                }
            }
            else
            {
                form.Add(new StringContent(VoiceHotwords.Joined(settings.Hotwords, 400)), "prompt");
            }
        }
        return form;
    }

    /// <summary>A <c>data:</c> line of the stream; anything else is an empty event.</summary>
    public static OpenAiEvent ParseEvent(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return default;
        var data = line.AsSpan(5).Trim();
        if (data.IsEmpty || data.SequenceEqual("[DONE]")) return default;
        try
        {
            using var document = JsonDocument.Parse(data.ToString());
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            var text = type switch
            {
                "transcript.text.delta" => root.TryGetProperty("delta", out var d) ? d.GetString() ?? "" : "",
                "transcript.text.done" => root.TryGetProperty("text", out var x) ? x.GetString() ?? "" : "",
                _ => "",
            };
            return new OpenAiEvent(type, text);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>The text of a plain JSON answer (<c>{"text": …}</c>).</summary>
    public static string TextOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>The message of an error answer (<c>{"error": {"message": …}}</c>); null when there is none.</summary>
    public static string? ErrorOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("error", out var error)
                   && error.ValueKind == JsonValueKind.Object
                   && error.TryGetProperty("message", out var message)
                ? message.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
