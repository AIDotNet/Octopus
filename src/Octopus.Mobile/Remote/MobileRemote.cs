using System.Reflection;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Client;
using Octopus.Remote.Protocol;
using Octopus.Remote.Relay;
using Octopus.Remote.Security;

namespace Octopus.Mobile.Remote;

/// <summary>
/// The phone on the real thing (design/12-远程操作.md §9): its key, its sign-in at the service (if any), the hosts it is
/// paired with and the connections to them. The same code as the desktop's device side (<see cref="RelaySession"/>,
/// <see cref="RemoteHostConnections"/>); what is the phone's own is where it keeps things and how it shows them.
/// </summary>
public sealed class MobileRemote : IDisposable
{
    private readonly RelaySession _session;
    private readonly RemoteHostConnections _connections;
    private readonly RemoteWorkspace _workspace;
    private readonly Lazy<Task<DeviceIdentity>> _identity;

    /// <param name="deviceName">What this phone calls itself to the service and to hosts ("Ada's iPhone").</param>
    /// <param name="officialServer">
    /// The official service's address, where "Sign in" goes without asking. Null takes the one this build was given
    /// (<see cref="MobileBuild.ServiceUrl"/>); empty means there is none.
    /// </param>
    public MobileRemote(IMobileStore store, string deviceName, string? officialServer = null)
    {
        var vault = new MobileVault(store);
        Common.MobilePreferences.Current.Load(store.DataDirectory);
        var settings = new MobileSettings(store.DataDirectory, officialServer ?? MobileBuild.ServiceUrl);
        _identity = new Lazy<Task<DeviceIdentity>>(() => DeviceIdentityStore.LoadOrCreateAsync(vault, Path.Combine(store.DataDirectory, "device.key")));
        _session = new RelaySession(() => settings.Server, vault, () => _identity.Value, kind: "phone");
        _connections = new RemoteHostConnections(
            () => _identity.Value,
            () => new PairedPeerStore(Path.Combine(store.DataDirectory, "hosts.json")),
            () => new DeviceDescription(deviceName, DeviceKind.Phone),
            Hello,
            _session);
        _workspace = new RemoteWorkspace(_session, _connections, settings, new MobileProjectIconCache(store.DataDirectory));
        Account = new RelayAccountSession(_session, settings, deviceName);
    }

    public IAccountSession Account { get; }
    public IRemoteWorkspace Workspace => _workspace;
    /// <summary>The connections to the hosts, for tests.</summary>
    internal RemoteHostConnections Connections => _connections;
    internal RelaySession Session => _session;

    /// <summary>The account's server with an access token that is good now; null when this phone is not signed in.</summary>
    public Task<RelayEndpoint?> GetServerAsync(bool fresh, CancellationToken cancellationToken) => _session.GetAsync(fresh, cancellationToken);

    /// <summary>What this app says about itself to a host.</summary>
    public static HelloMessage Hello { get; } = new()
    {
        Protocol = RemoteProtocol.Version,
        MinProtocol = RemoteProtocol.Version,
        MaxProtocol = RemoteProtocol.LatestVersion,
        AppVersion = typeof(MobileRemote).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "",
        Capabilities = [RemoteMethods.CapabilityWorkspace, RemoteMethods.CapabilityNativeSnapshot, RemoteMethods.CapabilitySizeRelease, RemoteMethods.CapabilityWorkspaces, RemoteMethods.CapabilityBrowse,
            RemoteMethods.CapabilityAcpGui, RemoteMethods.CapabilityAcpMentions,
            // Looking at the projects' files and their uncommitted changes (Files/).
            RemoteMethods.CapabilityFiles, RemoteMethods.CapabilityGit,
            // Pictures for a terminal session's program (Terminals/).
            RemoteMethods.CapabilityUpload,
            RemoteMethods.CapabilityProtocolV2, RemoteMethods.CapabilityCompression, RemoteMethods.CapabilityFileStream,
            RemoteMethods.CapabilitySessionResume, RemoteMethods.CapabilityTransportQuic, RemoteMethods.CapabilityTransportP2p,
            RemoteMethods.CapabilityTransportMigrate, RemoteMethods.CapabilityProjectIcons, RemoteMethods.CapabilityProjectGroups,
            RemoteMethods.CapabilityBots, RemoteMethods.CapabilityBotGroups],
        TerminalBuild = typeof(TKCode.Terminal.Core.TerminalCore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        Transport = TransportNegotiation.CreateDefault(),
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

/// <summary>What this build of the app was given when it was compiled.</summary>
public static class MobileBuild
{
    /// <summary>
    /// The official service's address: the MSBuild property <c>OctopusServiceUrl</c> (Directory.Build.props), kept in
    /// this assembly's metadata. Null while there is none.
    /// </summary>
    public static string? ServiceUrl { get; } = typeof(MobileBuild).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "OctopusServiceUrl")?.Value is { Length: > 0 } url ? url : null;
}

/// <summary>
/// The phone's few settings, each a small file of the data folder: the address of a service of the person's own
/// (<c>server.txt</c>) and whether the app is used without an account (<c>mode.txt</c>: empty or "local").
/// </summary>
internal sealed class MobileSettings
{
    private const string Local = "local";
    private readonly string _serverFile;
    private readonly string _modeFile;
    private string _custom;
    private bool _localOnly;

    /// <param name="official">The official service's address; null, empty or not an address when there is none.</param>
    public MobileSettings(string directory, string? official)
    {
        _serverFile = Path.Combine(directory, "server.txt");
        _modeFile = Path.Combine(directory, "mode.txt");
        Official = RelayAccountClient.ParseUrl(official) is null ? null : official!.Trim();
        var kept = Read(_serverFile);
        _custom = IsOfficial(kept) ? "" : kept;
        _localOnly = Read(_modeFile) == Local;
    }

    /// <summary>The official service's address; null when this build has none.</summary>
    public string? Official { get; }

    /// <summary>The address of a service of the person's own, as they typed it; empty when they use the official one or none.</summary>
    public string Custom => _custom;

    /// <summary>The service this phone signs in to: the person's own when they typed one, otherwise the official one. Empty when there is neither.</summary>
    public string Server => _custom.Length > 0 ? _custom : Official ?? "";

    /// <summary>The app is used without an account: the hosts are those this phone paired with itself.</summary>
    public bool LocalOnly
    {
        get => _localOnly;
        set
        {
            if (_localOnly == value) return;
            _localOnly = value;
            Write(_modeFile, value ? Local : "");
            LocalOnlyChanged?.Invoke();
        }
    }

    /// <summary>Raised on the thread that changed <see cref="LocalOnly"/>.</summary>
    public event Action? LocalOnlyChanged;

    /// <summary>The service to sign in to from now on. The official one is not kept: it is what an empty file means.</summary>
    public void Use(string server)
    {
        var typed = server.Trim();
        _custom = IsOfficial(typed) ? "" : typed;
        Write(_serverFile, _custom);
    }

    private bool IsOfficial(string server) =>
        Official is not null && RelayAccountClient.ParseUrl(server) is { } url && url == RelayAccountClient.ParseUrl(Official);

    private static string Read(string file)
    {
        try { return File.Exists(file) ? File.ReadAllText(file).Trim() : ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static void Write(string file, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
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
        settings.LocalOnlyChanged += () => Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsSignedIn => _session.Account is not null;
    public bool IsLocalOnly => !IsSignedIn && _settings.LocalOnly;
    public string? Name => _session.Account?.Name;
    public string? OfficialServer => _settings.Official;
    public string Server => _settings.Custom;
    public bool SignInEnded => _session.SignInEnded;
    public event EventHandler? Changed;

    public async Task<SignInChallenge> BeginSignInAsync(string server, CancellationToken cancellationToken = default)
    {
        if (RelayAccountClient.ParseUrl(server) is not { } url) throw new IOException("That is not an address.");
        // Another service than before: what was kept of the old one is of no use there.
        if (url != RelayAccountClient.ParseUrl(_settings.Server)) await _session.ForgetAsync().ConfigureAwait(false);
        _settings.Use(server);
        var login = await _session.BeginSignInAsync(_deviceName, cancellationToken).ConfigureAwait(false);
        var cancel = new CancellationTokenSource();
        return new SignInChallenge(login.UserCode, login.VerificationUriComplete, CompleteAsync(login, cancel.Token), cancel.Cancel);
    }

    public async Task SignInWithKeyAsync(string server, string accessKey, CancellationToken cancellationToken = default)
    {
        if (RelayAccountClient.ParseUrl(server) is not { } url) throw new IOException("That is not an address.");
        if (url != RelayAccountClient.ParseUrl(_settings.Server)) await _session.ForgetAsync().ConfigureAwait(false);
        _settings.Use(server);
        await _session.SignInWithKeyAsync(accessKey, _deviceName, cancellationToken).ConfigureAwait(false);
    }

    public void SignOut() => _ = _session.SignOutAsync();

    public void UseLocalOnly() => _settings.LocalOnly = true;

    public void LeaveLocalOnly() => _settings.LocalOnly = false;

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
