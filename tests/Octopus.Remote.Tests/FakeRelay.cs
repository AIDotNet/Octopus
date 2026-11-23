using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Octopus.Remote.Relay;
using Octopus.Remote.Security;

namespace Octopus.Remote.Tests;

/// <summary>
/// A relay as OctopusServer/design/01-中转协议.md and 03-账号与登录.md describe it, written again here from those texts
/// (the two repositories share no code): enough of it to run this app's side against, and it keeps every message it
/// carried so that a test can look at what a relay gets to see. The test plays the person in the browser:
/// <see cref="Approve"/> and <see cref="Deny"/>.
/// </summary>
internal sealed class FakeRelay : IAsyncDisposable
{
    /// <summary>An access token that is always good, of an account of its own: for tests that are not about signing in.</summary>
    public const string Token = "fake-relay-token-0123456789";
    private const string TokenAccount = "shared";

    private sealed class Login
    {
        public required string UserCode;
        public required DevicePublicKey Key;
        public required string Name;
        public string Kind = "desktop";
        public string? ApprovedBy;
        public bool Denied;
    }

    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, (WebSocket Socket, string Account)> _hosts = new();
    private readonly ConcurrentDictionary<string, (TaskCompletionSource<WebSocket> Host, TaskCompletionSource<WebSocket> Device)> _tunnels = new();
    private readonly ConcurrentBag<WebSocket> _sockets = [];
    private readonly ConcurrentDictionary<string, Login> _logins = new();
    // What is signed in: refresh token → (account, device), access token → the same.
    private readonly ConcurrentDictionary<string, (string Account, string DeviceId)> _refresh = new();
    private readonly ConcurrentDictionary<string, (string Account, string DeviceId)> _access = new();
    // The devices that signed in, as an account's list shows them.
    private readonly ConcurrentDictionary<string, (string Account, string Name, string Kind)> _devices = new();

    public FakeRelay()
    {
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = AcceptAsync();
    }

    public int Port { get; }
    public RelayEndpoint Endpoint => RelayEndpoint.TryCreate($"http://127.0.0.1:{Port}", Token)!;
    /// <summary>Every message that went through a tunnel, either way, as the relay had it in its hands.</summary>
    public ConcurrentQueue<byte[]> Carried { get; } = new();
    public int Registrations;
    public IReadOnlyCollection<string> Hosts => [.. _hosts.Keys];
    public string Url => $"http://127.0.0.1:{Port}";
    /// <summary>The codes of the sign-ins that wait for an answer, as the devices show them.</summary>
    public IReadOnlyCollection<string> PendingCodes => [.. _logins.Values.Where(l => l.ApprovedBy is null && !l.Denied).Select(l => l.UserCode)];
    /// <summary>The names the devices gave when they started signing in.</summary>
    public ConcurrentQueue<string> LoginNames { get; } = new();
    /// <summary>What each device said it runs on when it started signing in: its platform and the app's version, as sent (null when left out).</summary>
    public ConcurrentQueue<(string? Platform, string? AppVersion)> LoginClients { get; } = new();
    /// <summary>How many requests arrived, of any kind: zero means nothing ever asked this relay for anything.</summary>
    public int Requests;
    public int Refreshes;
    /// <summary>How long the access tokens say they live.</summary>
    public int AccessSeconds { get; set; } = 900;

    /// <summary>The person confirms a code in the browser, signed in as <paramref name="account"/>.</summary>
    public bool Approve(string userCode, string account = "ada")
    {
        var login = _logins.Values.FirstOrDefault(l => l.UserCode == userCode && l.ApprovedBy is null && !l.Denied);
        if (login is null) return false;
        login.ApprovedBy = account;
        return true;
    }

    public bool Deny(string userCode)
    {
        var login = _logins.Values.FirstOrDefault(l => l.UserCode == userCode && l.ApprovedBy is null && !l.Denied);
        if (login is null) return false;
        login.Denied = true;
        return true;
    }

    /// <summary>Every access token given out so far stops working, as when their time is up: the devices have to refresh.</summary>
    public void ExpireAccessTokens() => _access.Clear();

    /// <summary>The device is removed from its account: its sign-in ends and its control connection is closed with the reason.</summary>
    public void SignOut(string deviceId)
    {
        foreach (var (token, session) in _refresh) if (session.DeviceId == deviceId) _refresh.TryRemove(token, out _);
        foreach (var (token, session) in _access) if (session.DeviceId == deviceId) _access.TryRemove(token, out _);
        _devices.TryRemove(deviceId, out _);
        if (_hosts.TryRemove(deviceId, out var host)) _ = host.Socket.CloseOutputAsync((WebSocketCloseStatus)4403, "signed out", default);
    }

    /// <summary>Ends every host's control connection, as a relay that restarts does.</summary>
    public void DropHosts()
    {
        foreach (var (id, host) in _hosts)
        {
            _hosts.TryRemove(id, out _);
            // Said with a close: this listener's Abort leaves the connection itself open, which a client only notices
            // by its keep-alive, much later than a test waits.
            _ = host.Socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "restarting", default);
        }
    }

    private async Task AcceptAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(context); }
                catch (Exception e) when (e is WebSocketException or IOException or HttpListenerException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
            });
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        Interlocked.Increment(ref Requests);
        var path = context.Request.Url!.AbsolutePath;
        if (path.StartsWith("/v1/auth/", StringComparison.Ordinal) && context.Request.HttpMethod == "POST")
        {
            await HandleAuthAsync(context, path);
            return;
        }
        // Everything else is for who is signed in: the caller's account, and the device the token was given to.
        var bearer = context.Request.Headers["Authorization"] is { } header && header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : "";
        (string Account, string? DeviceId) caller;
        if (bearer == Token) caller = (TokenAccount, null);
        else if (_access.TryGetValue(bearer, out var signedIn)) caller = signedIn;
        else
        {
            Respond(context, 401, """{"error":"unauthorized"}""");
            return;
        }
        if (path == "/v1/devices" && context.Request.HttpMethod == "GET")
        {
            var devices = _devices.Where(device => device.Value.Account == caller.Account).OrderBy(device => device.Value.Name, StringComparer.Ordinal).Select(device => new Dictionary<string, object>
            {
                ["id"] = device.Key, ["name"] = device.Value.Name, ["kind"] = device.Value.Kind,
                ["online"] = _hosts.ContainsKey(device.Key), ["current"] = device.Key == caller.DeviceId,
                ["createdAt"] = DateTimeOffset.UtcNow, ["lastSeenAt"] = DateTimeOffset.UtcNow,
            }).ToList();
            Respond(context, 200, JsonSerializer.Serialize(new Dictionary<string, object> { ["devices"] = devices }));
        }
        else if (path == "/v1/host" && context.Request.IsWebSocketRequest)
        {
            var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            _sockets.Add(socket);
            var nonce = RandomNumberGenerator.GetBytes(32);
            await SendTextAsync(socket, JsonSerializer.Serialize(new Dictionary<string, string> { ["type"] = "challenge", ["nonce"] = Convert.ToBase64String(nonce) }));
            var register = JsonSerializer.Deserialize<JsonElement>((await ReadAsync(socket))!);
            var key = DevicePublicKey.Parse(Convert.FromBase64String(register.GetProperty("publicKey").GetString()!));
            // The hash is put together here from the protocol's text, not with the app's own helper.
            var hash = SHA256.HashData([.. Encoding.ASCII.GetBytes("octopus-relay/1 host\n"), .. nonce]);
            if (register.GetProperty("type").GetString() != "register" || !key.Verify(hash, Convert.FromBase64String(register.GetProperty("signature").GetString()!))
                || (caller.DeviceId is not null && caller.DeviceId != key.DeviceId))
            {
                await socket.CloseOutputAsync((WebSocketCloseStatus)4401, "bad registration", default);
                return;
            }
            Interlocked.Increment(ref Registrations);
            _hosts[key.DeviceId] = (socket, caller.Account);
            await SendTextAsync(socket, JsonSerializer.Serialize(new Dictionary<string, string> { ["type"] = "registered", ["deviceId"] = key.DeviceId }));
            try
            {
                while (await ReadAsync(socket) is not null) { }
            }
            finally
            {
                _hosts.TryRemove(new KeyValuePair<string, (WebSocket, string)>(key.DeviceId, (socket, caller.Account)));
            }
        }
        else if (path == "/v1/tunnels" && context.Request.HttpMethod == "POST")
        {
            var request = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.InputStream);
            // Somebody else's host is as good as offline.
            if (!_hosts.TryGetValue(request.GetProperty("hostDeviceId").GetString()!, out var host) || host.Account != caller.Account)
            {
                Respond(context, 404, """{"error":"host_offline"}""");
                return;
            }
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            _tunnels[id] = (new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
            await SendTextAsync(host.Socket, JsonSerializer.Serialize(new Dictionary<string, string> { ["type"] = "connect", ["tunnel"] = id }));
            Respond(context, 201, JsonSerializer.Serialize(new Dictionary<string, string> { ["tunnel"] = id }));
        }
        else if (path.StartsWith("/v1/tunnel/", StringComparison.Ordinal) && context.Request.IsWebSocketRequest && _tunnels.TryGetValue(path["/v1/tunnel/".Length..], out var tunnel))
        {
            var isHost = context.Request.QueryString["role"] == "host";
            var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            _sockets.Add(socket);
            (isHost ? tunnel.Host : tunnel.Device).SetResult(socket);
            var peer = await (isHost ? tunnel.Device : tunnel.Host).Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                while (await ReadAsync(socket) is { } message)
                {
                    Carried.Enqueue(message);
                    await peer.SendAsync(message, WebSocketMessageType.Binary, true, default);
                }
            }
            finally
            {
                try { await peer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, default); }
                catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException or IOException) { }
                peer.Abort();
            }
        }
        else
        {
            Respond(context, 404, """{"error":"not_found"}""");
        }
    }

    // Signing in (03-账号与登录.md §4, §5): a device starts with its key, a person confirms its code, and the device,
    // proving the key, gets its tokens; later it exchanges the refresh token, which works once.
    private async Task HandleAuthAsync(HttpListenerContext context, string path)
    {
        var request = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.InputStream);
        string? Text(string name) => request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        switch (path)
        {
            case "/v1/auth/device":
                var deviceCode = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
                var letters = RandomNumberGenerator.GetString("BCDFGHJKLMNPQRSTVWXZ", 8);
                var login = new Login { UserCode = $"{letters[..4]}-{letters[4..]}", Key = DevicePublicKey.Parse(Convert.FromBase64String(Text("publicKey")!)), Name = Text("deviceName") ?? "", Kind = Text("kind") == "phone" ? "phone" : "desktop" };
                _logins[deviceCode] = login;
                LoginNames.Enqueue(login.Name);
                LoginClients.Enqueue((Text("platform"), Text("appVersion")));
                Respond(context, 200, JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["deviceCode"] = deviceCode, ["userCode"] = login.UserCode, ["verificationUri"] = $"{Url}/device",
                    ["verificationUriComplete"] = $"{Url}/device?code={login.UserCode}", ["expiresIn"] = 600, ["interval"] = 1,
                }));
                break;
            case "/v1/auth/token" when Text("grantType") == "device_code":
                if (Text("deviceCode") is not { } code || !_logins.TryGetValue(code, out var pending))
                {
                    Respond(context, 400, """{"error":"expired_token"}""");
                }
                else if (Text("signature") is not { } signature || !pending.Key.Verify(SHA256.HashData([.. Encoding.ASCII.GetBytes("octopus-relay/1 login\n"), .. Encoding.ASCII.GetBytes(code)]), Convert.FromBase64String(signature)))
                {
                    Respond(context, 400, """{"error":"invalid_grant"}""");
                }
                else if (pending.Denied)
                {
                    _logins.TryRemove(code, out _);
                    Respond(context, 400, """{"error":"access_denied"}""");
                }
                else if (pending.ApprovedBy is { } account)
                {
                    _logins.TryRemove(code, out _);
                    _devices[pending.Key.DeviceId] = (account, pending.Name, pending.Kind);
                    Respond(context, 200, Issue(account, pending.Key.DeviceId));
                }
                else
                {
                    Respond(context, 400, """{"error":"authorization_pending"}""");
                }
                break;
            case "/v1/auth/token" when Text("grantType") == "refresh_token":
                if (Text("refreshToken") is { } refresh && _refresh.TryRemove(refresh, out var session))
                {
                    Interlocked.Increment(ref Refreshes);
                    Respond(context, 200, Issue(session.Account, session.DeviceId));
                }
                else
                {
                    Respond(context, 400, """{"error":"invalid_grant"}""");
                }
                break;
            case "/v1/auth/revoke":
                if (Text("refreshToken") is { } revoked && _refresh.TryRemove(revoked, out var ended)) SignOut(ended.DeviceId);
                context.Response.StatusCode = 204;
                context.Response.Close();
                break;
            default:
                Respond(context, 400, """{"error":"unsupported_grant_type"}""");
                break;
        }
    }

    private string Issue(string account, string deviceId)
    {
        var access = "oa1." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var refresh = "or1." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        _access[access] = (account, deviceId);
        _refresh[refresh] = (account, deviceId);
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["accessToken"] = access, ["refreshToken"] = refresh, ["expiresIn"] = AccessSeconds, ["deviceId"] = deviceId,
            ["account"] = new Dictionary<string, string> { ["id"] = "id-" + account, ["name"] = char.ToUpperInvariant(account[0]) + account[1..], ["email"] = account + "@example.com" },
        });
    }

    private static void Respond(HttpListenerContext context, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    private static Task SendTextAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, default);

    private static async Task<byte[]?> ReadAsync(WebSocket socket)
    {
        using var content = new MemoryStream();
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer.AsMemory(), default);
            if (received.MessageType == WebSocketMessageType.Close) return null;
            content.Write(buffer, 0, received.Count);
            if (received.EndOfMessage) return content.ToArray();
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var socket in _sockets) socket.Abort();
        try { _listener.Stop(); }
        catch (ObjectDisposedException) { }
        _listener.Close();
        return ValueTask.CompletedTask;
    }
}
