using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Octopus.Remote.Relay;

namespace Octopus.Mobile.Voice;

/// <summary>The Octopus server of the account and an access token that is good now; null when not signed in.</summary>
public delegate Task<RelayEndpoint?> AccountEndpoint(bool fresh, CancellationToken cancellationToken);

/// <summary>What <c>GET /v1/voice</c> says: whether the server dictates for the account, and today's seconds.</summary>
public sealed record OctopusVoiceStatus(bool Available, string? Provider, int DailySeconds, int UsedSeconds);

/// <summary>
/// Dictation through the Octopus account (design/server/05 §2「语音输入」): the phone streams its microphone to the account's
/// server, which forwards it with its own key. No key on the phone; the server keeps a daily allowance per account.
/// </summary>
internal sealed class OctopusDictationEngine(Func<IAudioCapture> audio, AccountEndpoint account, Func<bool> signedIn) : IDictationEngine
{
    public DictationProblem Check(VoiceSettings settings) => signedIn() ? DictationProblem.None : DictationProblem.NotSignedIn;

    public async Task<IDictationSession> StartAsync(VoiceSettings settings, CancellationToken cancellationToken)
    {
        var session = new OctopusDictationSession(settings, audio(), account);
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

    /// <summary>The server's answer to <c>GET /v1/voice</c>; null when it cannot be asked (not signed in, offline, an older server).</summary>
    public static async Task<OctopusVoiceStatus?> StatusAsync(AccountEndpoint account, Func<HttpMessageHandler> http, CancellationToken cancellationToken)
    {
        try
        {
            if (await account(false, cancellationToken).ConfigureAwait(false) is not { } endpoint) return null;
            using var client = new HttpClient(http(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, OctopusVoiceProtocol.StatusUri(endpoint.Url));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", endpoint.Token);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json") return null;
            return OctopusVoiceProtocol.ParseStatus(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or RelayException or JsonException)
        {
            return null;
        }
    }
}

internal sealed class OctopusDictationSession(VoiceSettings settings, IAudioCapture capture, AccountEndpoint account) : CloudDictationSession(settings, capture)
{
    protected override async Task<string> RunAsync(ChannelReader<byte[]> audio, CancellationToken cancellationToken)
    {
        using var socket = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await socket.SendAsync(Encoding.UTF8.GetBytes(OctopusVoiceProtocol.Start(Settings)), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        var receiving = ReceiveAsync(socket, cancellationToken);
        await foreach (var chunk in audio.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            // The server stops taking audio at its limits and answers at once: no use sending more.
            if (receiving.IsCompleted) break;
            await socket.SendAsync(chunk, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        if (!receiving.IsCompleted)
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(OctopusVoiceProtocol.Finish), WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        var text = await receiving.ConfigureAwait(false);
        await CloudWire.CloseQuietlyAsync(socket).ConfigureAwait(false);
        return text;
    }

    // A token the server refuses is made anew once: it may have run out a moment ago.
    private async Task<ClientWebSocket> ConnectAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            RelayEndpoint? endpoint;
            try
            {
                endpoint = await account(attempt > 0, cancellationToken).ConfigureAwait(false);
            }
            catch (RelayException e)
            {
                throw new DictationException(DictationErrorKind.Network, e.Message, e);
            }
            if (endpoint is null) throw new DictationException(DictationErrorKind.NotSignedIn, "not signed in");

            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + endpoint.Token);
            socket.Options.CollectHttpResponseDetails = true;
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(OctopusVoiceProtocol.StreamUri(endpoint.Url), timeout.Token).ConfigureAwait(false);
                return socket;
            }
            catch (WebSocketException e)
            {
                var status = socket.HttpStatusCode;
                socket.Dispose();
                if (status == HttpStatusCode.Unauthorized && attempt == 0) continue;
                throw new DictationException(OctopusVoiceProtocol.KindOf(status), status == 0 ? e.Message : $"HTTP {(int)status}", e);
            }
        }
    }

    private async Task<string> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (true)
        {
            var (type, data) = await CloudWire.ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
            if (type == WebSocketMessageType.Close) throw new IOException("The Octopus server closed the connection.");
            if (type != WebSocketMessageType.Text) continue;
            var message = OctopusVoiceProtocol.Parse(data);
            switch (message.Type)
            {
                case "text":
                    Report(message.Settled, message.Tail);
                    break;
                case "done":
                    return message.Text;
                case "error":
                    throw new DictationException(OctopusVoiceProtocol.KindOf(message.Code), message.Message);
            }
        }
    }
}

/// <summary>One message of the server: <c>text</c>, <c>done</c> or <c>error</c>.</summary>
internal readonly record struct OctopusVoiceMessage(string Type, string Settled, string Tail, string Text, string Code, string Message);

/// <summary>The messages of <c>/v1/voice/stream</c>, written and read without reflection (NativeAOT).</summary>
internal static class OctopusVoiceProtocol
{
    public static Uri StatusUri(Uri server) => new(server, "v1/voice");

    public static Uri StreamUri(Uri server) =>
        new UriBuilder(new Uri(server, "v1/voice/stream")) { Scheme = server.Scheme == "https" ? "wss" : "ws" }.Uri;

    public const string Finish = """{"type":"finish"}""";

    public static string Start(VoiceSettings settings)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("type", "start");
            if (settings.LanguageCode is { } language) json.WriteString("language", language);
            else json.WriteNull("language");
            json.WriteBoolean("punctuation", settings.Punctuation);
            json.WriteStartArray("hotwords");
            foreach (var word in settings.Hotwords) json.WriteStringValue(word);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static OctopusVoiceMessage Parse(ReadOnlySpan<byte> utf8)
    {
        using var document = JsonDocument.Parse(utf8.ToArray());
        var root = document.RootElement;
        return new OctopusVoiceMessage(Get(root, "type"), Get(root, "settled"), Get(root, "tail"), Get(root, "text"), Get(root, "code"), Get(root, "message"));
    }

    public static OctopusVoiceStatus? ParseStatus(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("available", out var available)) return null;
        return new OctopusVoiceStatus(
            available.ValueKind == JsonValueKind.True,
            root.TryGetProperty("provider", out var provider) && provider.ValueKind == JsonValueKind.String ? provider.GetString() : null,
            root.TryGetProperty("dailySeconds", out var daily) && daily.TryGetInt32(out var d) ? d : 0,
            root.TryGetProperty("usedSeconds", out var used) && used.TryGetInt32(out var u) ? u : 0);
    }

    /// <summary>A refused upgrade (its status) as the kind of error the person sees.</summary>
    public static DictationErrorKind KindOf(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => DictationErrorKind.NotSignedIn,
        HttpStatusCode.NotFound => DictationErrorKind.ServerOff,
        HttpStatusCode.TooManyRequests => DictationErrorKind.Quota,
        0 => DictationErrorKind.Network,
        _ => DictationErrorKind.Provider,
    };

    /// <summary>The server's <c>error</c> code as the kind of error the person sees.</summary>
    public static DictationErrorKind KindOf(string code) => code switch
    {
        "no_speech" => DictationErrorKind.NoSpeech,
        "timeout" => DictationErrorKind.Network,
        _ => DictationErrorKind.Provider,
    };

    private static string Get(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
