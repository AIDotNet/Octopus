using System.Text.Json;
using System.Text.Json.Serialization;
using Octopus.Remote.Security;

namespace Octopus.Remote.Relay;

/// <summary>What is kept of a sign-in at a relay: as one value, wherever the device keeps its secrets.</summary>
internal sealed class RelaySessionData
{
    /// <summary>The relay the sign-in belongs to: it is good for no other.</summary>
    public string Url { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string? AccountEmail { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RelaySessionData))]
internal sealed partial class RelaySessionJsonContext : JsonSerializerContext;

public enum RelaySignInResult
{
    SignedIn,
    /// <summary>The person refused in the browser.</summary>
    Denied,
    /// <summary>Nobody confirmed in time.</summary>
    Expired,
    Cancelled,
}

/// <summary>Where a device keeps its sign-in: the system's keystore, or a file only its user can read.</summary>
public interface IRelaySessionStore
{
    Task<string?> LoadAsync();
    /// <summary>Keeps the value; null removes it.</summary>
    Task SaveAsync(string? value);
}

/// <summary>What a connection through a relay asks of the sign-in: is there one, and a token that is good right now.</summary>
public interface IRelayGate
{
    /// <summary>True when a relay is set up and this device is signed in to it. Asks for no token.</summary>
    Task<bool> IsUsableAsync();
    /// <summary>See <see cref="RelaySession.GetAsync"/>.</summary>
    Task<RelayEndpoint?> GetAsync(bool fresh = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// This device's sign-in at a relay (design/12-远程操作.md §8; the relay's design/03-账号与登录.md): who it is signed in
/// as, the refresh token (kept in <see cref="IRelaySessionStore"/>), and an access token that is replaced before it
/// runs out. The desktop app and the phone app both use this; each brings its own store and its own way to the address.
/// </summary>
/// <param name="url">The relay's address as it is set right now.</param>
/// <param name="kind">What this device says it is when signing in: "desktop" or "phone".</param>
public sealed class RelaySession(Func<string?> url, IRelaySessionStore store, Func<Task<DeviceIdentity>> identity, string kind = "desktop") : IRelayGate
{
    /// <summary>An access token is replaced this long before it runs out: a request under way does not arrive with a dead one.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;
    private RelaySessionData? _session;
    private string? _accessToken;
    private DateTimeOffset _accessExpiresAt;

    /// <summary>The sign-in changed. Raised on the thread that changed it.</summary>
    public event Action? Changed;

    /// <summary>The relay's address as it is set right now (as typed; may be none).</summary>
    public string Url => url() ?? "";

    /// <summary>Who this device is signed in as at the relay; null when it is not (known only after <see cref="LoadAsync"/>).</summary>
    public RelayAccountInfo? Account => _session is { } session ? new RelayAccountInfo(session.AccountId, session.AccountName, session.AccountEmail) : null;

    /// <summary>
    /// True when the relay ended this device's sign-in (it was removed from the account, signed out everywhere, or
    /// not used for too long) and nobody has signed in again since.
    /// </summary>
    public bool SignInEnded { get; private set; }

    /// <summary>Reads the kept sign-in, once.</summary>
    public async Task LoadAsync()
    {
        if (_loaded) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            try
            {
                var text = await store.LoadAsync().ConfigureAwait(false);
                var session = string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize(text, RelaySessionJsonContext.Default.RelaySessionData);
                // A sign-in is good at the relay it was made at, and nowhere else.
                _session = session is { RefreshToken.Length: > 0, AccountId.Length: > 0 } && RelayAccountClient.ParseUrl(session.Url) is { } at && at == RelayAccountClient.ParseUrl(Url) ? session : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or JsonException)
            {
                _session = null;
            }
            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The relay with an access token that is good right now; null when no address is set or this device is not
    /// signed in. With <paramref name="fresh"/>, the token is made anew even when the one at hand has not run out (the
    /// relay refused it). Throws <see cref="RelayException"/> when a new token is needed and the relay cannot be reached.
    /// </summary>
    public async Task<RelayEndpoint?> GetAsync(bool fresh = false, CancellationToken cancellationToken = default)
    {
        await LoadAsync().ConfigureAwait(false);
        if (RelayAccountClient.ParseUrl(Url) is not { } url || _session is null) return null;
        var ended = false;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // One refresh at a time: a refresh token works once, and two requests with the same one would end the sign-in.
            if (_session is not { } session) return null;
            if (!fresh && _accessToken is { } current && DateTimeOffset.UtcNow < _accessExpiresAt - Margin) return new RelayEndpoint(url, current);
            var tokens = await RelayAccountClient.RefreshAsync(url, session.RefreshToken, cancellationToken).ConfigureAwait(false);
            if (tokens is null)
            {
                // The relay does not know this sign-in any more.
                (_session, _accessToken, SignInEnded, ended) = (null, null, true, true);
                await KeepAsync(null).ConfigureAwait(false);
                return null;
            }
            await AdoptAsync(url, tokens).ConfigureAwait(false);
            return new RelayEndpoint(url, tokens.AccessToken);
        }
        finally
        {
            _gate.Release();
            if (ended) Changed?.Invoke();
        }
    }

    /// <summary>
    /// The address is about to become another: a sign-in belongs to one relay, so this device is signed out of the one
    /// it has (there, too). To be called before the new address is in place.
    /// </summary>
    public async Task ForgetAsync()
    {
        await LoadAsync().ConfigureAwait(false);
        await SignOutCoreAsync().ConfigureAwait(false);
        SignInEnded = false;
    }

    /// <summary>
    /// Starts signing this device in: the code to show and the page to open. Throws <see cref="RelayException"/>
    /// (and <see cref="InvalidOperationException"/> when no address is set).
    /// </summary>
    public async Task<RelayDeviceLogin> BeginSignInAsync(string deviceName, CancellationToken cancellationToken = default)
    {
        var url = RelayAccountClient.ParseUrl(Url) ?? throw new InvalidOperationException("No relay address is set.");
        return await RelayAccountClient.StartLoginAsync(url, await identity().ConfigureAwait(false), deviceName, kind, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for the person to confirm the code in a browser, asking the relay at the pace it set. Throws
    /// <see cref="RelayException"/> when the relay cannot be reached for good or refuses this device's key.
    /// </summary>
    public async Task<RelaySignInResult> CompleteSignInAsync(RelayDeviceLogin login, CancellationToken cancellationToken = default)
    {
        var url = RelayAccountClient.ParseUrl(Url) ?? throw new InvalidOperationException("No relay address is set.");
        var device = await identity().ConfigureAwait(false);
        var interval = PollInterval ?? login.Interval;
        var unreachable = 0;
        try
        {
            while (DateTimeOffset.UtcNow < login.ExpiresAt)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                RelayLoginStatus status;
                RelayTokens? tokens;
                try
                {
                    (status, tokens) = await RelayAccountClient.PollLoginAsync(url, login, device, cancellationToken).ConfigureAwait(false);
                    unreachable = 0;
                }
                catch (RelayException e) when (e.Failure == RelayFailure.Unreachable && ++unreachable < 5)
                {
                    // A hiccup of the network while the person is in the browser: the code is still good.
                    continue;
                }
                switch (status)
                {
                    case RelayLoginStatus.SignedIn:
                        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try { await AdoptAsync(url, tokens!).ConfigureAwait(false); }
                        finally { _gate.Release(); }
                        SignInEnded = false;
                        Changed?.Invoke();
                        return RelaySignInResult.SignedIn;
                    case RelayLoginStatus.Denied:
                        return RelaySignInResult.Denied;
                    case RelayLoginStatus.Expired:
                        return RelaySignInResult.Expired;
                    case RelayLoginStatus.SlowDown:
                        interval += TimeSpan.FromSeconds(PollInterval is null ? 5 : 0.05);
                        break;
                }
            }
            return RelaySignInResult.Expired;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RelaySignInResult.Cancelled;
        }
    }

    /// <summary>How often the relay is asked while a sign-in waits; null: as the relay says. Shortened in tests.</summary>
    public TimeSpan? PollInterval { get; set; }

    /// <summary>Signs this device out: at the relay (when it can be reached) and here.</summary>
    public async Task SignOutAsync()
    {
        await LoadAsync().ConfigureAwait(false);
        await SignOutCoreAsync().ConfigureAwait(false);
        SignInEnded = false;
        Changed?.Invoke();
    }

    private async Task SignOutCoreAsync()
    {
        RelaySessionData? session;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            session = _session;
            (_session, _accessToken) = (null, null);
            await KeepAsync(null).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        if (session is not null && RelayAccountClient.ParseUrl(session.Url) is { } url) await RelayAccountClient.RevokeAsync(url, session.RefreshToken).ConfigureAwait(false);
    }

    // Under the gate. The new refresh token is kept before the access token is used: the one before it is dead already.
    private async Task AdoptAsync(Uri url, RelayTokens tokens)
    {
        var session = new RelaySessionData
        {
            Url = url.ToString(),
            RefreshToken = tokens.RefreshToken,
            AccountId = tokens.Account.Id,
            AccountName = tokens.Account.Name,
            AccountEmail = tokens.Account.Email,
        };
        (_session, _accessToken, _accessExpiresAt) = (session, tokens.AccessToken, tokens.AccessExpiresAt);
        await KeepAsync(session).ConfigureAwait(false);
    }

    // What cannot be written is still good for as long as the app runs: failing here must not lose a sign-in that works.
    private async Task KeepAsync(RelaySessionData? session)
    {
        try
        {
            await store.SaveAsync(session is null ? null : JsonSerializer.Serialize(session, RelaySessionJsonContext.Default.RelaySessionData)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
        }
    }

    /// <summary>True when a relay address is set and this device is signed in there. Asks for no token.</summary>
    public async Task<bool> IsUsableAsync()
    {
        await LoadAsync().ConfigureAwait(false);
        return _session is not null && RelayAccountClient.ParseUrl(Url) is not null;
    }
}
