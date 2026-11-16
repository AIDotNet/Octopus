using System.Net;
using System.Net.Http.Headers;
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
public sealed record RelayDevice(string Id, string Name, string Kind, bool Online, bool Current, DateTimeOffset LastSeenAt);

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

internal sealed class RelayDeviceLoginRequest
{
    public string? DeviceName { get; set; }
    public string? Kind { get; set; }
    public string? PublicKey { get; set; }
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
}

internal sealed class RelayDevicesResponse
{
    public List<RelayDeviceBody>? Devices { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RelayDevicesResponse))]
[JsonSerializable(typeof(RelayDeviceLoginRequest))]
[JsonSerializable(typeof(RelayDeviceLoginResponse))]
[JsonSerializable(typeof(RelayTokenRequest))]
[JsonSerializable(typeof(RelayTokenResponse))]
internal sealed partial class RelayAccountJsonContext : JsonSerializerContext;

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
    private const int MaxAnswerBytes = 16 * 1024;

    /// <summary>The relay's address from what a person types (see <see cref="RelayEndpoint.TryCreate"/>); null when it is none.</summary>
    public static Uri? ParseUrl(string? typed) => RelayEndpoint.TryCreate(typed, "-")?.Url;

    /// <summary>Starts a sign-in for this device. Throws <see cref="RelayException"/>.</summary>
    public static async Task<RelayDeviceLogin> StartLoginAsync(Uri relay, DeviceIdentity identity, string deviceName, string kind = "desktop", CancellationToken cancellationToken = default)
    {
        var request = new RelayDeviceLoginRequest { DeviceName = deviceName, Kind = kind, PublicKey = Convert.ToBase64String(identity.PublicKey.Bytes) };
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
            .Select(d => new RelayDevice(d.Id!, d.Name is { Length: > 0 and <= 128 } name ? name : d.Id!, d.Kind == "phone" ? "phone" : "desktop", d.Online, d.Current, d.LastSeenAt))];
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
