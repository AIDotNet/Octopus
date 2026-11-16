using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Octopus.Remote.Security;
using Octopus.Remote.Transport;

namespace Octopus.Remote.Relay;

/// <summary>
/// A relay service and what lets this computer use it right now (design/12-远程操作.md §8; the relay's own protocol is
/// written down in OctopusServer/design/01-中转协议.md): the access token of the account this device is signed in to.
/// Access tokens live for minutes, so an endpoint is asked for anew each time a connection is made.
/// </summary>
public sealed record RelayEndpoint(Uri Url, string Token)
{
    /// <summary>
    /// From what a person types: <c>relay.example.com</c>, <c>https://…</c>; <c>http://</c> is taken as typed (a relay
    /// on this machine, or behind something that adds TLS). Null when it is no address or the token is missing.
    /// </summary>
    public static RelayEndpoint? TryCreate(string? url, string? token)
    {
        var text = url?.Trim() ?? "";
        var secret = token?.Trim() ?? "";
        if (text.Length == 0 || secret.Length == 0 || text.Contains(' ')) return null;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;
        var scheme = uri.Scheme switch { "https" or "wss" => "https", "http" or "ws" => "http", _ => null };
        if (scheme is null) return null;
        return new RelayEndpoint(new UriBuilder(uri) { Scheme = scheme, Port = uri.IsDefaultPort ? -1 : uri.Port, Path = uri.AbsolutePath.TrimEnd('/') + "/", Query = "", Fragment = "" }.Uri, secret);
    }

    internal Uri Http(string path) => new(Url, path);
    internal Uri WebSocket(string path) => new UriBuilder(new Uri(Url, path)) { Scheme = Url.Scheme == "https" ? "wss" : "ws" }.Uri;

    // The token never shows in a log or a debugger's list of values.
    public override string ToString() => Url.ToString();
}

public enum RelayFailure
{
    /// <summary>The relay cannot be reached.</summary>
    Unreachable,
    /// <summary>The relay does not accept the access token: this device is not signed in (any more).</summary>
    Unauthorized,
    /// <summary>The host is not connected to the relay.</summary>
    HostOffline,
    /// <summary>A limit of the relay was reached; later it may work.</summary>
    Busy,
    /// <summary>The account carried all it may this month.</summary>
    OverQuota,
    /// <summary>The relay said something this app does not understand.</summary>
    Protocol,
}

public sealed class RelayException(RelayFailure failure, string message, Exception? inner = null) : IOException(message, inner)
{
    public RelayFailure Failure { get; } = failure;
}

internal sealed class RelayControlMessage
{
    public string Type { get; set; } = "";
    public string? Nonce { get; set; }
    public string? PublicKey { get; set; }
    public string? Name { get; set; }
    public string? Signature { get; set; }
    public string? DeviceId { get; set; }
    public string? Tunnel { get; set; }
}

internal sealed class RelayOpenTunnel
{
    public string HostDeviceId { get; set; } = "";
}

internal sealed class RelayOpenedTunnel
{
    public string? Tunnel { get; set; }
    public string? Error { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RelayControlMessage))]
[JsonSerializable(typeof(RelayOpenTunnel))]
[JsonSerializable(typeof(RelayOpenedTunnel))]
internal sealed partial class RelayJsonContext : JsonSerializerContext;

/// <summary>What both sides do at a relay: open a WebSocket there with the access token.</summary>
internal static class RelaySockets
{
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);
    private static readonly byte[] RegistrationLabel = Encoding.ASCII.GetBytes("octopus-relay/1 host\n");

    public static async Task<ClientWebSocket> ConnectAsync(RelayEndpoint relay, string path, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = KeepAlive;
        // A relay that went away without a word is noticed by its missing answer.
        socket.Options.KeepAliveTimeout = KeepAlive;
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Authorization", "Bearer " + relay.Token);
        try
        {
            await socket.ConnectAsync(relay.WebSocket(path), cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch (Exception e) when (e is WebSocketException or IOException or HttpRequestException or InvalidOperationException)
        {
            var status = socket.HttpStatusCode;
            socket.Dispose();
            throw status switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new RelayException(RelayFailure.Unauthorized, "The relay does not accept the access token.", e),
                HttpStatusCode.TooManyRequests => new RelayException(RelayFailure.Busy, "The relay is busy; try again later.", e),
                HttpStatusCode.NotFound or HttpStatusCode.Conflict => new RelayException(RelayFailure.HostOffline, "The connection through the relay is no longer there.", e),
                _ => new RelayException(RelayFailure.Unreachable, $"The relay cannot be reached: {e.Message}", e),
            };
        }
    }

    /// <summary>What a host signs to register (the relay's design, §3): SHA-256 of the label and the relay's nonce.</summary>
    public static byte[] RegistrationHash(ReadOnlySpan<byte> nonce)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(RegistrationLabel);
        hash.AppendData(nonce);
        return hash.GetHashAndReset();
    }

    public static bool IsTunnelId(string? text) => text is { Length: >= 16 and <= 64 } && text.All(char.IsAsciiLetterOrDigit);
}

/// <summary>The device's way to a host through a relay: asks for a tunnel and connects to its end.</summary>
public static class RelayDialer
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(10) });

    /// <summary>
    /// A transport to the host, through the relay. What goes over it is the same secure channel as on the local
    /// network: the relay carries it without being able to read or change it. Throws <see cref="RelayException"/>.
    /// </summary>
    public static async Task<IMessageTransport> ConnectAsync(RelayEndpoint relay, string hostDeviceId, CancellationToken cancellationToken = default)
    {
        string tunnel;
        using (var request = new HttpRequestMessage(HttpMethod.Post, relay.Http("v1/tunnels")))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", relay.Token);
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(new RelayOpenTunnel { HostDeviceId = hostDeviceId }, RelayJsonContext.Default.RelayOpenTunnel))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            };
            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                throw new RelayException(RelayFailure.Unreachable, $"The relay cannot be reached: {e.Message}", e);
            }
            using (response)
            {
                RelayOpenedTunnel? body = null;
                try
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    if (bytes.Length is > 0 and < 4096) body = JsonSerializer.Deserialize(bytes, RelayJsonContext.Default.RelayOpenedTunnel);
                }
                catch (Exception e) when (e is JsonException or HttpRequestException or IOException) { }
                tunnel = response.StatusCode switch
                {
                    HttpStatusCode.Created when RelaySockets.IsTunnelId(body?.Tunnel) => body!.Tunnel!,
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => throw new RelayException(RelayFailure.Unauthorized, "The relay does not accept the access token."),
                    HttpStatusCode.NotFound => throw new RelayException(RelayFailure.HostOffline, "The host is not connected to the relay."),
                    HttpStatusCode.TooManyRequests when body?.Error == "quota" => throw new RelayException(RelayFailure.OverQuota, "The account has used up this month's traffic through the relay."),
                    HttpStatusCode.TooManyRequests => throw new RelayException(RelayFailure.Busy, "The relay is busy; try again later."),
                    _ => throw new RelayException(RelayFailure.Protocol, $"The relay answered {(int)response.StatusCode}."),
                };
            }
        }
        return new WebSocketTransport(await RelaySockets.ConnectAsync(relay, $"v1/tunnel/{tunnel}?role=device", cancellationToken).ConfigureAwait(false));
    }
}

public enum RelayLinkState
{
    Connecting,
    /// <summary>Registered: devices can reach this host through the relay.</summary>
    Online,
    /// <summary>Not connected; it is tried again by itself. <see cref="RelayHostLink.Error"/> says why.</summary>
    Failed,
}

/// <summary>
/// The host's standing connection to a relay: proves this device's key, then waits to be told that a device asks for
/// it, and meets each one in its tunnel. Kept up for as long as it lives: a lost connection is made again.
/// </summary>
public sealed class RelayHostLink : IAsyncDisposable
{
    private static readonly TimeSpan[] Pauses = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
    /// <summary>After what does not get better by trying soon: a refused token, the same device connected from elsewhere.</summary>
    private static readonly TimeSpan LongPause = TimeSpan.FromSeconds(60);
    private const int BadRegistrationCloseCode = 4401;
    private const int SignedOutCloseCode = 4403;
    private const int ReplacedCloseCode = 4409;
    private const int FullCloseCode = 4429;
    private const int AccountFullCloseCode = 4432;

    private readonly Func<bool, CancellationToken, Task<RelayEndpoint?>> _endpoint;
    private readonly DeviceIdentity _identity;
    private readonly Func<string> _name;
    private readonly Func<IMessageTransport, Task> _serve;
    private readonly CancellationTokenSource _stop = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IMessageTransport, bool> _serving = new();
    private Task? _running;

    /// <param name="endpoint">
    /// Asked before every connection for the relay and a token that is good right now; with true, for a token made
    /// anew (the one before was refused). Null: this device is not signed in.
    /// </param>
    /// <param name="serve">Given the transport of each device that comes through the relay; it does the handshake and everything after.</param>
    public RelayHostLink(Func<bool, CancellationToken, Task<RelayEndpoint?>> endpoint, DeviceIdentity identity, Func<string> name, Func<IMessageTransport, Task> serve) =>
        (_endpoint, _identity, _name, _serve) = (endpoint, identity, name, serve);

    /// <summary>With a token that stays good for as long as the link lives.</summary>
    public RelayHostLink(RelayEndpoint relay, DeviceIdentity identity, Func<string> name, Func<IMessageTransport, Task> serve)
        : this((_, _) => Task.FromResult<RelayEndpoint?>(relay), identity, name, serve)
    {
    }

    public RelayLinkState State { get; private set; } = RelayLinkState.Connecting;
    /// <summary>Why it is not online; null while it is or has not failed yet.</summary>
    public string? Error { get; private set; }
    /// <summary>The state changed. Raised on a background thread.</summary>
    public event Action? Changed;

    /// <summary>How long is waited before trying again; shortened in tests.</summary>
    internal TimeSpan? RetryPause { get; set; }

    public void Start() => _running ??= Task.Run(RunAsync);

    private async Task RunAsync()
    {
        var failures = 0;
        // After a refused token the next attempt asks for one made anew; refused again, there is nothing to try soon.
        var fresh = false;
        while (!_stop.IsCancellationRequested)
        {
            var pause = Pauses[Math.Min(failures, Pauses.Length - 1)];
            var askedFresh = fresh;
            try
            {
                Set(RelayLinkState.Connecting, Error);
                var relay = await _endpoint(fresh, _stop.Token).ConfigureAwait(false)
                            ?? throw new RelayException(RelayFailure.Unauthorized, "This computer is not signed in to the relay.");
                fresh = false;
                using var socket = await RelaySockets.ConnectAsync(relay, "v1/host", _stop.Token).ConfigureAwait(false);
                await RegisterAsync(socket).ConfigureAwait(false);
                failures = 0;
                pause = Pauses[0];
                Set(RelayLinkState.Online, null);
                while (await ReceiveAsync(socket, _stop.Token).ConfigureAwait(false) is { } message)
                {
                    if (message.Type == "connect" && RelaySockets.IsTunnelId(message.Tunnel)) _ = MeetAsync(message.Tunnel!);
                }
                switch ((int?)socket.CloseStatus)
                {
                    case ReplacedCloseCode:
                        pause = LongPause;
                        throw new RelayException(RelayFailure.Busy, "This device is connected to the relay from somewhere else (another copy of the app with the same key?).");
                    case SignedOutCloseCode:
                        throw new RelayException(RelayFailure.Unauthorized, "This computer was signed out of the relay.");
                    default:
                        throw new RelayException(RelayFailure.Unreachable, "The relay closed the connection.");
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is IOException or WebSocketException or JsonException or OperationCanceledException or InvalidOperationException or FormatException)
            {
                failures++;
                if (e is RelayException { Failure: RelayFailure.Unauthorized })
                {
                    // Once more right away with a new token (the old one may only have run out); then seldom.
                    fresh = !askedFresh;
                    pause = askedFresh ? LongPause : Pauses[0];
                }
                Set(RelayLinkState.Failed, e is OperationCanceledException ? "The relay did not answer in time." : e.Message);
            }
            try { await Task.Delay(RetryPause ?? pause, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RegisterAsync(ClientWebSocket socket)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        var challenge = await ReceiveAsync(socket, limit.Token).ConfigureAwait(false);
        if (challenge is not { Type: "challenge", Nonce: { } encoded }) throw new RelayException(RelayFailure.Protocol, "The relay did not ask for this device's key.");
        var nonce = Convert.FromBase64String(encoded);
        // Exactly 32 bytes are signed, under a label of their own: nothing a relay sends here can pass for a signature
        // of the secure channel's handshake.
        if (nonce.Length != 32) throw new RelayException(RelayFailure.Protocol, "The relay's challenge is not of the expected length.");
        var register = new RelayControlMessage
        {
            Type = "register",
            PublicKey = Convert.ToBase64String(_identity.PublicKey.Bytes),
            Name = _name(),
            Signature = Convert.ToBase64String(_identity.Sign(RelaySockets.RegistrationHash(nonce))),
        };
        await socket.SendAsync((ReadOnlyMemory<byte>)JsonSerializer.SerializeToUtf8Bytes(register, RelayJsonContext.Default.RelayControlMessage), WebSocketMessageType.Text, true, limit.Token).ConfigureAwait(false);
        var answer = await ReceiveAsync(socket, limit.Token).ConfigureAwait(false);
        if (answer is null)
        {
            throw (int?)socket.CloseStatus switch
            {
                FullCloseCode => new RelayException(RelayFailure.Busy, "The relay has no room for another host."),
                AccountFullCloseCode => new RelayException(RelayFailure.Busy, "The account has as many computers connected to the relay as it may."),
                // The sign-in is another device's (this one's key changed since): signing in again sets it right.
                BadRegistrationCloseCode => new RelayException(RelayFailure.Unauthorized, "The relay does not accept this computer's key with its sign-in. Sign in to the relay again."),
                _ => new RelayException(RelayFailure.Protocol, "The relay refused this device's registration."),
            };
        }
        if (answer.Type != "registered" || answer.DeviceId != _identity.Id) throw new RelayException(RelayFailure.Protocol, "The relay registered this device under another id.");
    }

    // A device waits in the tunnel: this host comes to its other end and serves it like any device that connects.
    private async Task MeetAsync(string tunnel)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            if (await _endpoint(false, limit.Token).ConfigureAwait(false) is not { } relay) return;
            var transport = new WebSocketTransport(await RelaySockets.ConnectAsync(relay, $"v1/tunnel/{tunnel}?role=host", limit.Token).ConfigureAwait(false));
            _serving[transport] = true;
            try
            {
                if (_stop.IsCancellationRequested) await transport.DisposeAsync().ConfigureAwait(false);
                else await _serve(transport).ConfigureAwait(false);
            }
            finally
            {
                _serving.TryRemove(transport, out _);
            }
        }
        catch (Exception e) when (e is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The device gave up, or the relay did: it asks again if it still wants in.
        }
    }

    /// <summary>The next control message; null when the relay closed the connection.</summary>
    private static async Task<RelayControlMessage?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var length = 0;
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close) return null;
            length += received.Count;
            if (received.EndOfMessage) break;
            if (length == buffer.Length) throw new RelayException(RelayFailure.Protocol, "The relay sent a message that is too long.");
        }
        return JsonSerializer.Deserialize(buffer.AsSpan(0, length), RelayJsonContext.Default.RelayControlMessage)
               ?? throw new RelayException(RelayFailure.Protocol, "The relay sent an empty message.");
    }

    private void Set(RelayLinkState state, string? error)
    {
        if (State == state && Error == error) return;
        (State, Error) = (state, error);
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_running is { } running)
        {
            try { await running.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception e) when (e is TimeoutException or OperationCanceledException) { }
        }
        // Who came in through the relay leaves with it: turning relay access off ends their connections.
        foreach (var transport in _serving.Keys) await transport.DisposeAsync().ConfigureAwait(false);
    }
}
