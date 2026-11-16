using System.Reflection;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Client;
using Octopus.Remote.Protocol;
using Octopus.Remote.Relay;
using Octopus.Remote.Security;

namespace Octopus.Mobile.Remote;

/// <summary>
/// The phone on the real thing (design/12-远程操作.md §9): its key, its sign-in at the service, the hosts it is paired
/// with and the connections to them. The same code as the desktop's device side (<see cref="RelaySession"/>,
/// <see cref="RemoteHostConnections"/>); what is the phone's own is where it keeps things and how it shows them.
/// </summary>
public sealed class MobileRemote : IDisposable
{
    private readonly RelaySession _session;
    private readonly RemoteHostConnections _connections;
    private readonly RemoteWorkspace _workspace;
    private readonly Lazy<Task<DeviceIdentity>> _identity;

    /// <param name="deviceName">What this phone calls itself to the service and to hosts ("Ada's iPhone").</param>
    /// <param name="defaultServer">The service's address before the person typed one.</param>
    public MobileRemote(IMobileStore store, string deviceName, string? defaultServer = null)
    {
        var vault = new MobileVault(store);
        var settings = new MobileSettings(store.DataDirectory, defaultServer);
        _identity = new Lazy<Task<DeviceIdentity>>(() => DeviceIdentityStore.LoadOrCreateAsync(vault, Path.Combine(store.DataDirectory, "device.key")));
        _session = new RelaySession(() => settings.Server, vault, () => _identity.Value, kind: "phone");
        _connections = new RemoteHostConnections(
            () => _identity.Value,
            () => new PairedPeerStore(Path.Combine(store.DataDirectory, "hosts.json")),
            () => new DeviceDescription(deviceName, DeviceKind.Phone),
            Hello,
            _session);
        _workspace = new RemoteWorkspace(_session, _connections);
        Account = new RelayAccountSession(_session, settings, deviceName);
    }

    public IAccountSession Account { get; }
    public IRemoteWorkspace Workspace => _workspace;
    /// <summary>The connections to the hosts, for tests.</summary>
    internal RemoteHostConnections Connections => _connections;
    internal RelaySession Session => _session;

    /// <summary>What this app says about itself to a host.</summary>
    public static HelloMessage Hello { get; } = new()
    {
        AppVersion = typeof(MobileRemote).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "",
        Capabilities = [RemoteMethods.CapabilityWorkspace, RemoteMethods.CapabilityNativeSnapshot, RemoteMethods.CapabilitySizeRelease],
        TerminalBuild = typeof(TKCode.Terminal.Core.TerminalCore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    };

    /// <summary>
    /// Reads what is kept (is this phone signed in?) before the first page is chosen. Only the store is read, no
    /// network: it may be waited for where the app starts.
    /// </summary>
    public void Load() => Task.Run(() => _session.LoadAsync()).GetAwaiter().GetResult();

    public void Dispose()
    {
        _workspace.Dispose();
        _connections.Dispose();
    }
}

/// <summary>The one setting the phone has so far: the service's address, in a file of the data folder.</summary>
internal sealed class MobileSettings
{
    private readonly string _file;
    private string _server;

    public MobileSettings(string directory, string? defaultServer)
    {
        _file = Path.Combine(directory, "server.txt");
        string? kept = null;
        try { kept = File.Exists(_file) ? File.ReadAllText(_file).Trim() : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        _server = string.IsNullOrEmpty(kept) ? defaultServer?.Trim() ?? "" : kept;
    }

    public string Server
    {
        get => _server;
        set
        {
            _server = value.Trim();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                File.WriteAllText(_file, _server);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>The account as the pages see it, on the shared sign-in.</summary>
internal sealed class RelayAccountSession : IAccountSession
{
    private readonly RelaySession _session;
    private readonly MobileSettings _settings;
    private readonly string _deviceName;

    public RelayAccountSession(RelaySession session, MobileSettings settings, string deviceName)
    {
        (_session, _settings, _deviceName) = (session, settings, deviceName);
        session.Changed += () => Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsSignedIn => _session.Account is not null;
    public string? Name => _session.Account?.Name;
    public string Server => _settings.Server;
    public bool SignInEnded => _session.SignInEnded;
    public event EventHandler? Changed;

    public async Task<SignInChallenge> BeginSignInAsync(string server, CancellationToken cancellationToken = default)
    {
        if (RelayAccountClient.ParseUrl(server) is not { } url) throw new IOException("That is not an address.");
        // Another service than before: what was kept of the old one is of no use there.
        if (url != RelayAccountClient.ParseUrl(_settings.Server)) await _session.ForgetAsync().ConfigureAwait(false);
        _settings.Server = server;
        var login = await _session.BeginSignInAsync(_deviceName, cancellationToken).ConfigureAwait(false);
        var cancel = new CancellationTokenSource();
        return new SignInChallenge(login.UserCode, login.VerificationUriComplete, CompleteAsync(login, cancel.Token), cancel.Cancel);
    }

    public void SignOut() => _ = _session.SignOutAsync();

    private async Task<SignInOutcome> CompleteAsync(RelayDeviceLogin login, CancellationToken cancellationToken)
    {
        try
        {
            return await _session.CompleteSignInAsync(login, cancellationToken).ConfigureAwait(false) switch
            {
                RelaySignInResult.SignedIn => SignInOutcome.SignedIn,
                RelaySignInResult.Denied => SignInOutcome.Denied,
                RelaySignInResult.Expired => SignInOutcome.Expired,
                _ => SignInOutcome.Cancelled,
            };
        }
        catch (RelayException)
        {
            return SignInOutcome.Failed;
        }
    }
}
