using Octopus.Core;
using Octopus.Platform;
using Octopus.Remote.Relay;
using Octopus.Storage;

namespace Octopus.App.Remote;

/// <summary>
/// The relay this computer uses, if any (design/12-远程操作.md §8): its address from the settings (falling back to
/// the official service when not customized), and the account this computer is signed in to there (<see cref="RelaySession"/>,
/// shared with the phone app), kept in the system's keystore (a file only this user can read where there is none).
/// Both the host side (being reachable through the relay) and the device side (reaching hosts through it) ask here for
/// a token that is good right now.
/// </summary>
public sealed class RelayAccess : IRelayGate
{
    private const string Service = "Octopus/remote";
    private const string SessionName = "relay-session";
    /// <summary>The static access token of before accounts: no longer used, removed when found.</summary>
    private const string LegacyTokenName = "relay-token";

    private readonly ISettingsStore _settings;
    private readonly string? _officialUrl;
    private readonly RelaySession _session;

    public RelayAccess(ISettingsStore settings, ISecretStore secrets, IAppPaths paths, RemoteIdentityProvider identity, string? officialUrl = null)
    {
        _settings = settings;
        var resolvedOfficial = officialUrl ?? ServiceEndpoints.OfficialServiceUrl;
        _officialUrl = RelayAccountClient.ParseUrl(resolvedOfficial) is null ? null : resolvedOfficial!.Trim();
        _session = new RelaySession(ResolveUrl, new Keystore(secrets, paths), identity.GetAsync);
        _session.Changed += () => Changed?.Invoke();
    }

    /// <summary>The official relay address compiled into this build, or null when disabled.</summary>
    public string? OfficialUrl => _officialUrl;

    /// <summary>The address or the sign-in changed. Raised on the thread that changed them.</summary>
    public event Action? Changed;

    public string Url => _session.Url;
    /// <summary>Who this computer is signed in as at the relay; null when it is not (known only after <see cref="LoadAsync"/>).</summary>
    public RelayAccountInfo? Account => _session.Account;
    /// <summary>True when the relay ended this computer's sign-in and nobody has signed in again since.</summary>
    public bool SignInEnded => _session.SignInEnded;
    /// <summary>How often the relay is asked while a sign-in waits; null: as the relay says. Shortened in tests.</summary>
    internal TimeSpan? PollInterval { get => _session.PollInterval; set => _session.PollInterval = value; }

    public Task LoadAsync() => _session.LoadAsync();
    public Task<bool> IsUsableAsync() => _session.IsUsableAsync();
    public Task<RelayEndpoint?> GetAsync(bool fresh = false, CancellationToken cancellationToken = default) => _session.GetAsync(fresh, cancellationToken);
    public Task<RelayDeviceLogin> BeginSignInAsync(string deviceName, CancellationToken cancellationToken = default) => _session.BeginSignInAsync(deviceName, cancellationToken);
    public Task<RelaySignInResult> CompleteSignInAsync(RelayDeviceLogin login, CancellationToken cancellationToken = default) => _session.CompleteSignInAsync(login, cancellationToken);
    public Task SignOutAsync() => _session.SignOutAsync();

    /// <summary>Keeps the relay's address. A sign-in belongs to one relay: with another address this computer is signed out (of the old one, too).</summary>
    public async Task SetUrlAsync(string? url)
    {
        var typed = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
        var stored = typed is not null && IsOfficial(typed) ? null : typed;
        var effectiveNext = stored ?? _officialUrl;
        if (stored == _settings.Current.Remote.RelayUrl && effectiveNext == Url) return;
        if (RelayAccountClient.ParseUrl(effectiveNext) != RelayAccountClient.ParseUrl(Url)) await _session.ForgetAsync().ConfigureAwait(false);
        _settings.Update(s => s.Remote.RelayUrl = stored);
        Changed?.Invoke();
    }

    private string? ResolveUrl() => string.IsNullOrWhiteSpace(_settings.Current.Remote.RelayUrl) ? _officialUrl : _settings.Current.Remote.RelayUrl;

    private bool IsOfficial(string candidate) =>
        _officialUrl is not null && RelayAccountClient.ParseUrl(candidate) is { } parsed && parsed == RelayAccountClient.ParseUrl(_officialUrl);

    private sealed class Keystore(ISecretStore secrets, IAppPaths paths) : IRelaySessionStore
    {
        private string File => Path.Combine(paths.DataDirectory, "remote", "relay.session");

        public async Task<string?> LoadAsync()
        {
            if (!secrets.IsAvailable) return System.IO.File.Exists(File) ? await System.IO.File.ReadAllTextAsync(File).ConfigureAwait(false) : null;
            if (await secrets.GetAsync(Service, LegacyTokenName).ConfigureAwait(false) is not null) await secrets.DeleteAsync(Service, LegacyTokenName).ConfigureAwait(false);
            return await secrets.GetAsync(Service, SessionName).ConfigureAwait(false);
        }

        public async Task SaveAsync(string? value)
        {
            if (secrets.IsAvailable)
            {
                if (value is null) await secrets.DeleteAsync(Service, SessionName).ConfigureAwait(false);
                else await secrets.SetAsync(Service, SessionName, value).ConfigureAwait(false);
            }
            else if (value is null)
            {
                System.IO.File.Delete(File);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(File)!);
                var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using var stream = new FileStream(File, options);
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(value)).ConfigureAwait(false);
            }
        }
    }
}
