using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Octopus.Remote.Security;

namespace Octopus.Remote.Relay;

/// <summary>Who is signed in at a relay, as the relay names them.</summary>
public sealed record RelayAccountInfo(string Id, string Name, string? Email);

/// <summary>
/// What a relay gives a device that is signed in (the relay's design/03-账号与登录.md §5): a short-lived access token
/// for its requests, and a refresh token that gets the next pair and is replaced each time it is used.
/// </summary>
public sealed record RelayTokens(string AccessToken, string RefreshToken, DateTimeOffset AccessExpiresAt, RelayAccountInfo Account)
{
    // Tokens never show in a log or a debugger's list of values.
    public override string ToString() => $"tokens of {Account.Name}";
}

/// <summary>A sign-in this device started: the code to show the person and the page where they confirm it.</summary>
public sealed record RelayDeviceLogin(string DeviceCode, string UserCode, Uri VerificationUri, Uri VerificationUriComplete, DateTimeOffset ExpiresAt, TimeSpan Interval);

/// <summary>A computer or phone signed in to the same account, as the relay lists it.</summary>
/// <param name="Kind">"desktop" or "phone".</param>
/// <param name="Online">It is connected to the relay as a host right now.</param>
/// <param name="Current">It is the device that asks.</param>
/// <param name="PasswordAccess">While online: the host takes devices of the account that come with its access password (design/16).</param>
/// <param name="Platform">The system it runs ("macOS", "iOS"…), when it said.</param>
/// <param name="Features">Live host feature tags advertised while online (relay protocol v2).</param>
public sealed record RelayDevice(
    string Id,
    string Name,
    string Kind,
    bool Online,
    bool Current,
    DateTimeOffset LastSeenAt,
    bool PasswordAccess = false,
    string? Platform = null,
    IReadOnlyList<string>? Features = null);

public enum RelayLoginStatus
{
    /// <summary>Nobody confirmed yet: ask again after the interval.</summary>
    Pending,
    /// <summary>Asked too soon: wait longer between asking.</summary>
    SlowDown,
    /// <summary>The person refused.</summary>
    Denied,
    /// <summary>Too late: the code is no longer good.</summary>
    Expired,
    SignedIn,
}

/// <summary>Public capabilities advertised by a self-hosted relay at <c>GET /v1/info</c>.</summary>
public sealed record RelayServerInfo(
    int Protocol,
    string Mode,
    string Name,
    IReadOnlyList<string> Auth,
    IReadOnlyList<string>? Transports = null,
    IReadOnlyList<string>? StunServers = null,
    string? QuicEndpoint = null,
    int? IdleTimeoutSeconds = null)
{
    public bool SupportsDeviceCode => Auth.Contains("device_code", StringComparer.OrdinalIgnoreCase);
    public bool SupportsAccessKey => Auth.Contains("access_key", StringComparer.OrdinalIgnoreCase);
    public bool SupportsQuic => TryGetQuicUri() is not null && (Transports is null || Transports.Contains("quic", StringComparer.OrdinalIgnoreCase));

    /// <summary>Parses <see cref="QuicEndpoint"/> as a <c>quic://host:port</c> URI when advertised.</summary>
    public Uri? TryGetQuicUri() =>
        !string.IsNullOrWhiteSpace(QuicEndpoint)
        && Uri.TryCreate(QuicEndpoint, UriKind.Absolute, out var uri)
        && uri.Scheme.Equals("quic", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrEmpty(uri.Host)
        && uri.Port > 0
            ? uri
            : null;

    /// <summary>Resolves the first reachable <c>stun:host:port</c> entry in <see cref="StunServers"/> to an <see cref="IPEndPoint"/>.</summary>
    public async Task<IPEndPoint?> ResolveStunEndPointAsync(CancellationToken cancellationToken = default)
    {
        if (StunServers is null || StunServers.Count == 0) return null;
        foreach (var raw in StunServers)
        {
            if (TryParseStunHostPort(raw, out var host, out var port))
            {
                if (IPAddress.TryParse(host, out var ip)) return new IPEndPoint(ip, port);
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
                    var ipv4 = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    if (ipv4 is not null) return new IPEndPoint(ipv4, port);
                    if (addresses.Length > 0) return new IPEndPoint(addresses[0], port);
                }
                catch (Exception e) when (e is System.Net.Sockets.SocketException or OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested) throw;
                }
            }
        }
        return null;
    }

    internal static bool TryParseStunHostPort(string? raw, out string host, out int port)
    {
        host = "";
        port = 3478;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim();
        if (text.StartsWith("stun:", StringComparison.OrdinalIgnoreCase)) text = text["stun:".Length..];
        if (text.StartsWith("//", StringComparison.Ordinal)) text = text[2..];
        if (text.Length == 0) return false;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close <= 1) return false;
            host = text[1..close];
            var rest = text[(close + 1)..];
            if (rest.Length == 0) return true;
            if (rest[0] != ':' || !int.TryParse(rest[1..], out port) || port is <= 0 or > 65535) return false;
            return true;
        }
        var colon = text.LastIndexOf(':');
        if (colon < 0)
        {
            host = text;
            return true;
        }
        host = text[..colon];
        return host.Length > 0 && int.TryParse(text[(colon + 1)..], out port) && port is > 0 and <= 65535;
    }
}

/// <summary>
/// A one-click self-hosted relay setup link (<c>octopus://relay?v=1&amp;url=…&amp;key=…</c>): carries the relay URL
/// and optionally an Access Key so desktop and mobile apps can bind in one paste without a browser.
/// </summary>
public sealed record RelayConfigLink(Uri Url, string? AccessKey)
{
    public const string Scheme = "octopus";

    public static RelayConfigLink? TryParse(string? raw)
    {
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text) || !text.StartsWith("octopus://relay", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("relay", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string? urlText = null;
        string? key = null;
        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
            switch (name)
            {
                case "url":
                    urlText = value;
                    break;
                case "key":
                    key = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                    break;
            }
        }

        var parsedUrl = RelayAccountClient.ParseUrl(urlText);
        return parsedUrl is null ? null : new RelayConfigLink(parsedUrl, key);
    }

    public override string ToString() => Url.ToString();
}

internal sealed class RelayInfoResponse
{
    public int Protocol { get; set; }
    public string? Mode { get; set; }
    public string? Name { get; set; }
    public List<string>? Auth { get; set; }
    public List<string>? Transports { get; set; }
    public List<string>? StunServers { get; set; }
    public string? QuicEndpoint { get; set; }
    public int? IdleTimeoutSeconds { get; set; }
}

internal sealed class RelayKeyChallengeResponse
{
    public string? Challenge { get; set; }
    public int ExpiresIn { get; set; }
}

internal sealed class RelayKeyLoginRequest
{
    public string? AccessKey { get; set; }
    public string? Challenge { get; set; }
    public string? DeviceName { get; set; }
    public string? Kind { get; set; }
    public string? PublicKey { get; set; }
    public string? Signature { get; set; }
    public string? Platform { get; set; }
    public string? AppVersion { get; set; }
}

internal sealed class RelayDeviceLoginRequest
{
    public string? DeviceName { get; set; }
    public string? Kind { get; set; }
    public string? PublicKey { get; set; }
    /// <summary>The system this device runs ("macOS", "iOS"…); left out when it is none the relay knows.</summary>
    public string? Platform { get; set; }
    /// <summary>The app's version; left out when it has none.</summary>
    public string? AppVersion { get; set; }
}

internal sealed class RelayDeviceLoginResponse
{
    public string? DeviceCode { get; set; }
    public string? UserCode { get; set; }
    public string? VerificationUri { get; set; }
    public string? VerificationUriComplete { get; set; }
    public int ExpiresIn { get; set; }
    public int Interval { get; set; }
}

internal sealed class RelayTokenRequest
{
    public string? GrantType { get; set; }
    public string? DeviceCode { get; set; }
    public string? Signature { get; set; }
    public string? RefreshToken { get; set; }
}

internal sealed class RelayAccountBody
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Email { get; set; }
}

internal sealed class RelayTokenResponse
{
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public int ExpiresIn { get; set; }
    public RelayAccountBody? Account { get; set; }
    public string? Error { get; set; }
}

internal sealed class RelayDeviceBody
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Kind { get; set; }
    public bool Online { get; set; }
    public bool Current { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public bool PasswordAccess { get; set; }
    public string? Platform { get; set; }
    public List<string>? Features { get; set; }
}

internal sealed class RelayDevicesResponse
{
    public List<RelayDeviceBody>? Devices { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RelayInfoResponse))]
[JsonSerializable(typeof(RelayKeyChallengeResponse))]
[JsonSerializable(typeof(RelayKeyLoginRequest))]
[JsonSerializable(typeof(RelayDevicesResponse))]
[JsonSerializable(typeof(RelayDeviceLoginRequest))]
[JsonSerializable(typeof(RelayDeviceLoginResponse))]
[JsonSerializable(typeof(RelayTokenRequest))]
[JsonSerializable(typeof(RelayTokenResponse))]
internal sealed partial class RelayAccountJsonContext : JsonSerializerContext;

/// <summary>What a device says about itself when it signs in, for the account's list of devices (the relay's design/04-设备管理.md §6).</summary>
public static class RelayClientInfo
{
    /// <summary>The system this app runs on, as the relay names it; null on any other.</summary>
    public static string? Platform { get; } =
        OperatingSystem.IsIOS() ? "iOS"
        : OperatingSystem.IsAndroid() ? "Android"
        : OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsLinux() ? "Linux"
        : null;

    /// <summary>
    /// The app's version: the informational version of the program that was started (on a phone, where there is no
    /// such program, of this library, which is built with the same one), without the commit after "+". Null when there is none.
    /// </summary>
    public static string? AppVersion { get; } = VersionOf(
        (Assembly.GetEntryAssembly() ?? typeof(RelayClientInfo).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>"1.2.3+4f5e6d7" is "1.2.3"; nothing, or something too long to be a version, is null.</summary>
    internal static string? VersionOf(string? informational)
    {
        var version = informational?.Split('+', 2)[0].Trim();
        return version is { Length: > 0 and <= 64 } ? version : null;
    }
}

/// <summary>
/// Signing this device in at a relay (the relay's design/03-账号与登录.md §4): the device asks for a code and shows
/// it, the person confirms it in a browser where they are signed in, and the device, proving its key, is given its
/// tokens. Nothing is typed into the app, and the app receives no link back.
/// </summary>
public static class RelayAccountClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), ConnectTimeout = TimeSpan.FromSeconds(10) })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };
    private static readonly byte[] LoginLabel = Encoding.ASCII.GetBytes("octopus-relay/1 login\n");
    private static readonly byte[] KeyLoginLabel = Encoding.ASCII.GetBytes("octopus-relay/1 key-login\n");
    private const int MaxAnswerBytes = 16 * 1024;

    /// <summary>The relay's address from what a person types (see <see cref="RelayEndpoint.TryCreate"/>); null when it is none.</summary>
    public static Uri? ParseUrl(string? typed) => RelayEndpoint.TryCreate(typed, "-")?.Url;

    /// <summary>
    /// Queries <c>GET /v1/info</c> to discover capabilities of a self-hosted relay. Returns null when the endpoint is
    /// absent (404, such as older servers that only support device-code login). Throws <see cref="RelayException"/> on network failure.
    /// </summary>
    public static async Task<RelayServerInfo?> GetInfoAsync(Uri relay, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relay, "v1/info"));
        HttpStatusCode status;
        byte[] bytes;
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            if (status == HttpStatusCode.NotFound) return null;
            if (response.Content.Headers.ContentLength > MaxAnswerBytes) return null;
            bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException || e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new RelayException(RelayFailure.Unreachable, e is OperationCanceledException ? "The relay did not answer in time." : $"The relay cannot be reached: {e.Message}", e);
        }
        if (status != HttpStatusCode.OK || bytes.Length > MaxAnswerBytes) return null;
        var parsed = Parse(bytes, RelayAccountJsonContext.Default.RelayInfoResponse);
        if (parsed is not { Protocol: >= 1, Auth.Count: > 0 }) return null;
        return new RelayServerInfo(
            parsed.Protocol,
            parsed.Mode ?? "cloud",
            parsed.Name ?? relay.Host,
            parsed.Auth,
            parsed.Transports,
            parsed.StunServers,
            parsed.QuicEndpoint,
            parsed.IdleTimeoutSeconds);
    }

    /// <summary>
    /// Signs this device in at a self-hosted relay using its Access Key (challenge + device ECDSA signature), returning
    /// the same short-lived access token and rotating refresh token as device-code sign-in. Throws <see cref="RelayException"/>.
    /// </summary>
    public static async Task<RelayTokens> SignInWithKeyAsync(Uri relay, string accessKey, DeviceIdentity identity, string deviceName, string kind = "desktop", CancellationToken cancellationToken = default)
    {
        var key = accessKey?.Trim() ?? "";
        if (key.Length == 0) throw new RelayException(RelayFailure.Unauthorized, "Access key is required.");

        var (challengeStatus, challengeBytes) = await PostAsync(new Uri(relay, "v1/auth/key/challenge"), "{}"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        if (challengeStatus == HttpStatusCode.TooManyRequests) throw new RelayException(RelayFailure.Busy, "The relay is busy; try again later.");
        if (challengeStatus is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            throw new RelayException(RelayFailure.Protocol, "This relay does not accept access-key sign-in.");
        }

        var challengeAnswer = challengeStatus == HttpStatusCode.OK ? Parse(challengeBytes, RelayAccountJsonContext.Default.RelayKeyChallengeResponse) : null;
        if (challengeAnswer?.Challenge is not { Length: > 0 and <= 64 } challengeEncoded)
        {
            throw new RelayException(RelayFailure.Protocol, $"The relay answered {(int)challengeStatus} to the key challenge.");
        }

        byte[] challenge;
        try { challenge = Convert.FromBase64String(challengeEncoded); }
        catch (FormatException) { throw new RelayException(RelayFailure.Protocol, "The relay sent an invalid challenge."); }
        if (challenge.Length != 32) throw new RelayException(RelayFailure.Protocol, "The relay's challenge is not of the expected length.");

        var loginRequest = new RelayKeyLoginRequest
        {
            AccessKey = key,
            Challenge = challengeEncoded,
            DeviceName = deviceName,
            Kind = kind,
            PublicKey = Convert.ToBase64String(identity.PublicKey.Bytes),
            Signature = Convert.ToBase64String(identity.Sign(KeyLoginHash(challenge))),
            Platform = RelayClientInfo.Platform,
            AppVersion = RelayClientInfo.AppVersion,
        };
        var (status, bytes) = await PostAsync(new Uri(relay, "v1/auth/key"), JsonSerializer.SerializeToUtf8Bytes(loginRequest, RelayAccountJsonContext.Default.RelayKeyLoginRequest), cancellationToken).ConfigureAwait(false);
        var answer = Parse(bytes, RelayAccountJsonContext.Default.RelayTokenResponse);
        if (status == HttpStatusCode.OK && Tokens(answer) is { } tokens) return tokens;
        throw status switch
        {
            HttpStatusCode.Unauthorized => new RelayException(RelayFailure.Unauthorized, "The access key was not accepted by the relay."),
            HttpStatusCode.Forbidden => new RelayException(RelayFailure.Protocol, "This relay does not accept access-key sign-in."),
            HttpStatusCode.Conflict when answer?.Error == "device_limit" => new RelayException(RelayFailure.Busy, "This relay has reached its maximum device limit."),
            HttpStatusCode.TooManyRequests => new RelayException(RelayFailure.Busy, "The relay is busy; try again later."),
            _ => new RelayException(RelayFailure.Protocol, $"The relay answered {(int)status} to the access-key sign-in."),
        };
    }

    /// <summary>What a device signs to authenticate an access-key login: SHA-256 of the label and the 32-byte challenge.</summary>
    internal static byte[] KeyLoginHash(ReadOnlySpan<byte> challenge)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(KeyLoginLabel);
        hash.AppendData(challenge);
        return hash.GetHashAndReset();
    }

    /// <summary>Starts a sign-in for this device. Throws <see cref="RelayException"/>.</summary>
    public static async Task<RelayDeviceLogin> StartLoginAsync(Uri relay, DeviceIdentity identity, string deviceName, string kind = "desktop", CancellationToken cancellationToken = default)
    {
        // The system and the version are only shown to the person, in the account's list of devices: a relay that
        // does not know these two fields ignores them.
        var request = new RelayDeviceLoginRequest
        {
            DeviceName = deviceName,
            Kind = kind,
            PublicKey = Convert.ToBase64String(identity.PublicKey.Bytes),
            Platform = RelayClientInfo.Platform,
            AppVersion = RelayClientInfo.AppVersion,
        };
        var (status, bytes) = await PostAsync(new Uri(relay, "v1/auth/device"), JsonSerializer.SerializeToUtf8Bytes(request, RelayAccountJsonContext.Default.RelayDeviceLoginRequest), cancellationToken).ConfigureAwait(false);
        if (status == HttpStatusCode.TooManyRequests) throw new RelayException(RelayFailure.Busy, "The relay is busy; try again later.");
        var answer = status == HttpStatusCode.OK ? Parse(bytes, RelayAccountJsonContext.Default.RelayDeviceLoginResponse) : null;
        if (answer is not { DeviceCode: { Length: > 0 and <= 128 } deviceCode, UserCode: { Length: > 0 and <= 32 } userCode }
            || !deviceCode.All(char.IsAscii)
            || PageUri(answer.VerificationUri) is not { } page
            || PageUri(answer.VerificationUriComplete ?? answer.VerificationUri) is not { } complete)
        {
            throw new RelayException(RelayFailure.Protocol, status == HttpStatusCode.OK
                ? "The relay's answer to the sign-in is not one this app understands."
                : $"The relay does not offer sign-in (it answered {(int)status}).");
        }
        return new RelayDeviceLogin(deviceCode, userCode, page, complete,
            DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Clamp(answer.ExpiresIn, 30, 3600)), TimeSpan.FromSeconds(Math.Clamp(answer.Interval, 1, 60)));
    }

    /// <summary>
    /// Asks how the sign-in stands, proving this device's key. On <see cref="RelayLoginStatus.SignedIn"/> the tokens
    /// come along. Throws <see cref="RelayException"/> when the relay cannot be reached or refuses the proof.
    /// </summary>
    public static async Task<(RelayLoginStatus Status, RelayTokens? Tokens)> PollLoginAsync(Uri relay, RelayDeviceLogin login, DeviceIdentity identity, CancellationToken cancellationToken = default)
    {
        var request = new RelayTokenRequest
        {
            GrantType = "device_code",
            DeviceCode = login.DeviceCode,
            Signature = Convert.ToBase64String(identity.Sign(LoginHash(login.DeviceCode))),
        };
        var (status, answer) = await TokenAsync(relay, request, cancellationToken).ConfigureAwait(false);
        if (status == HttpStatusCode.OK && Tokens(answer) is { } tokens) return (RelayLoginStatus.SignedIn, tokens);
        return answer?.Error switch
        {
            "authorization_pending" => (RelayLoginStatus.Pending, null),
            "slow_down" => (RelayLoginStatus.SlowDown, null),
            "access_denied" => (RelayLoginStatus.Denied, null),
            "expired_token" => (RelayLoginStatus.Expired, null),
            "invalid_grant" => throw new RelayException(RelayFailure.Unauthorized, "The relay does not accept this device's key for the sign-in."),
            _ when status == HttpStatusCode.TooManyRequests => (RelayLoginStatus.SlowDown, null),
            _ => throw new RelayException(RelayFailure.Protocol, $"The relay answered {(int)status} to the sign-in."),
        };
    }

    /// <summary>
    /// Exchanges the refresh token for the next pair. Null when the relay no longer accepts it: the device has to sign
    /// in again. Throws <see cref="RelayException"/> when the relay cannot be reached (the token is still good then).
    /// </summary>
    public static async Task<RelayTokens?> RefreshAsync(Uri relay, string refreshToken, CancellationToken cancellationToken = default)
    {
        var (status, answer) = await TokenAsync(relay, new RelayTokenRequest { GrantType = "refresh_token", RefreshToken = refreshToken }, cancellationToken).ConfigureAwait(false);
        if (status == HttpStatusCode.OK && Tokens(answer) is { } tokens) return tokens;
        if (status == HttpStatusCode.BadRequest && answer?.Error is "invalid_grant") return null;
        throw status == HttpStatusCode.TooManyRequests
            ? new RelayException(RelayFailure.Busy, "The relay is busy; try again later.")
            : new RelayException(RelayFailure.Protocol, $"The relay answered {(int)status} to the request for a new token.");
    }

    /// <summary>
    /// The devices signed in to the account of the access token (the relay's design/03 §6). Throws
    /// <see cref="RelayException"/>: <see cref="RelayFailure.Unauthorized"/> when the token is not accepted.
    /// </summary>
    public static async Task<IReadOnlyList<RelayDevice>> ListDevicesAsync(RelayEndpoint relay, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relay.Http("v1/devices"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", relay.Token);
        HttpStatusCode status;
        byte[] bytes;
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            bytes = response.Content.Headers.ContentLength > 1024 * 1024 ? [] : await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException || e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new RelayException(RelayFailure.Unreachable, e is OperationCanceledException ? "The relay did not answer in time." : $"The relay cannot be reached: {e.Message}", e);
        }
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new RelayException(RelayFailure.Unauthorized, "The relay does not accept the access token.");
        if (status == HttpStatusCode.TooManyRequests) throw new RelayException(RelayFailure.Busy, "The relay is busy; try again later.");
        var answer = status == HttpStatusCode.OK ? Parse(bytes, RelayAccountJsonContext.Default.RelayDevicesResponse) : null;
        if (answer?.Devices is not { } devices) throw new RelayException(RelayFailure.Protocol, $"The relay answered {(int)status} to the request for the account's devices.");
        // Only what looks like a device: an id of the form the apps derive, and a name of a bounded length.
        return [.. devices.Where(d => d.Id is { Length: 26 } id && id.All(char.IsAsciiLetterOrDigit))
            .Select(d => new RelayDevice(d.Id!, d.Name is { Length: > 0 and <= 128 } name ? name : d.Id!, d.Kind == "phone" ? "phone" : "desktop", d.Online, d.Current, d.LastSeenAt,
                d.Online && d.PasswordAccess, d.Platform is { Length: > 0 and <= 32 } platform ? platform : null, d.Online ? d.Features : null))];
    }

    /// <summary>Signs this device out at the relay. Best effort: a relay that cannot be reached is not waited for.</summary>
    public static async Task RevokeAsync(Uri relay, string refreshToken, CancellationToken cancellationToken = default)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(8));
            await PostAsync(new Uri(relay, "v1/auth/revoke"),
                JsonSerializer.SerializeToUtf8Bytes(new RelayTokenRequest { RefreshToken = refreshToken }, RelayAccountJsonContext.Default.RelayTokenRequest), limit.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is RelayException or OperationCanceledException)
        {
        }
    }

    /// <summary>What a device signs to finish signing in: SHA-256 of the label and the device code's characters.</summary>
    internal static byte[] LoginHash(string deviceCode)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(LoginLabel);
        hash.AppendData(Encoding.ASCII.GetBytes(deviceCode));
        return hash.GetHashAndReset();
    }

    private static async Task<(HttpStatusCode Status, RelayTokenResponse? Answer)> TokenAsync(Uri relay, RelayTokenRequest request, CancellationToken cancellationToken)
    {
        var (status, bytes) = await PostAsync(new Uri(relay, "v1/auth/token"), JsonSerializer.SerializeToUtf8Bytes(request, RelayAccountJsonContext.Default.RelayTokenRequest), cancellationToken).ConfigureAwait(false);
        return (status, Parse(bytes, RelayAccountJsonContext.Default.RelayTokenResponse));
    }

    private static RelayTokens? Tokens(RelayTokenResponse? answer) =>
        answer is { AccessToken: { Length: > 0 and <= 1024 } access, RefreshToken: { Length: > 0 and <= 512 } refresh, Account: { Id: { Length: > 0 } id } account }
            ? new RelayTokens(access, refresh, DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Clamp(answer.ExpiresIn, 30, 86400)),
                new RelayAccountInfo(id, string.IsNullOrWhiteSpace(account.Name) ? id : account.Name!, string.IsNullOrWhiteSpace(account.Email) ? null : account.Email))
            : null;

    private static async Task<(HttpStatusCode Status, byte[] Body)> PostAsync(Uri address, byte[] body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, address);
        request.Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > MaxAnswerBytes) return (response.StatusCode, []);
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return (response.StatusCode, bytes.Length > MaxAnswerBytes ? [] : bytes);
        }
        catch (Exception e) when (e is HttpRequestException or IOException || e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new RelayException(RelayFailure.Unreachable, e is OperationCanceledException ? "The relay did not answer in time." : $"The relay cannot be reached: {e.Message}", e);
        }
    }

    private static T? Parse<T>(byte[] bytes, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        if (bytes.Length == 0) return null;
        try { return JsonSerializer.Deserialize(bytes, type); }
        catch (JsonException) { return null; }
    }

    // The page a browser is sent to: only ever a web address.
    private static Uri? PageUri(string? text) =>
        text is { Length: <= 512 } && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : null;
}
