using System.Collections.ObjectModel;
using Octopus.Terminal;

namespace Octopus.Mobile.Workspace;

/// <summary>
/// The hosts this phone knows and what runs on them: the one seam between the phone's pages and the remote protocol
/// (design/12-远程操作.md §6). The pages only use this; <see cref="Demo.DemoWorkspace"/> stands in for the preview and
/// the layout tests. Every collection and property changes on the UI thread.
/// </summary>
public partial interface IRemoteWorkspace
{
    /// <summary>
    /// Paired hosts, and while signed in the account's other computers that are not paired yet. Without an account
    /// (<see cref="IAccountSession.IsLocalOnly"/>) only the hosts this phone paired with itself.
    /// </summary>
    ObservableCollection<RemoteHost> Hosts { get; }

    /// <summary>True when this phone is paired with a computer already, whether or not <see cref="Hosts"/> shows it right now.</summary>
    bool HasPairedHosts => Hosts.Any(host => host.IsPaired);

    /// <summary>
    /// While signed in, the account's devices that are not computers to operate: phones, this one too
    /// (<see cref="AccountDevice.IsCurrent"/>). Only shown, never connected to. Empty without an account.
    /// </summary>
    ObservableCollection<AccountDevice> OtherDevices { get; }

    /// <summary>
    /// Gets into a computer of the account this phone is not paired with, with the access password shown in that
    /// computer's settings (design/16-账号设备与访问密码.md). Once it worked the computer is remembered like a paired
    /// one (<see cref="RemoteHost.IsPaired"/>, and connected); the owner can revoke it there. Only through the
    /// account's relay, so only while signed in. The password may be typed with spaces, dashes and any case.
    /// </summary>
    Task<AccessOutcome> AccessWithPasswordAsync(RemoteHost host, string password, CancellationToken cancellationToken = default);

    /// <summary>Starts a terminal or agent in a project on its host; the new session is also added to the project.</summary>
    Task<RemoteSession> CreateSessionAsync(RemoteProject project, NewSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Loads git branches and optional GitHub issues/PRs from the host for creating a new branch workspace.</summary>
    Task<RemoteWorkspaceOptions> GetWorkspaceOptionsAsync(RemoteProject project, CancellationToken cancellationToken = default) =>
        Task.FromResult(RemoteWorkspaceOptions.Empty);

    /// <summary>Creates a new isolated git worktree workspace on the host and adds it to the project.</summary>
    Task<RemoteBranchWorkspace> CreateWorkspaceAsync(RemoteProject project, CreateBranchWorkspaceRequest request, CancellationToken cancellationToken = default) =>
        throw new IOException("Branch workspaces are not supported.");

    /// <summary>Gives a session another title on the host; null or empty goes back to the one it had by default.</summary>
    Task RenameSessionAsync(RemoteSession session, string? title, CancellationToken cancellationToken = default) =>
        throw new IOException("Renaming is not supported.");

    /// <summary>Ends a session on the host (its program stops) and takes it out of its project.</summary>
    Task CloseSessionAsync(RemoteSession session, CancellationToken cancellationToken = default) =>
        throw new IOException("Closing is not supported.");

    /// <summary>Starts a session's program again on the host; an open terminal page sees its screen cleared.</summary>
    Task RestartSessionAsync(RemoteSession session, CancellationToken cancellationToken = default) =>
        throw new IOException("Restarting is not supported.");

    /// <summary>
    /// Lists the folders of a host for picking a new project. Null starts at the host's home; empty is the list of
    /// drives on Windows. Throws <see cref="IOException"/> (with words for the person) when the host cannot.
    /// </summary>
    Task<RemoteFolderListing> BrowseFolderAsync(RemoteHost host, string? path, CancellationToken cancellationToken = default) =>
        throw new IOException("Browsing is not supported.");

    /// <summary>Makes an empty folder while browsing; returns its full path on the host.</summary>
    Task<string> CreateFolderAsync(RemoteHost host, string parentPath, string name, CancellationToken cancellationToken = default) =>
        throw new IOException("Browsing is not supported.");

    /// <summary>Makes a folder of the host a project there; it joins <see cref="RemoteHost.Projects"/>.</summary>
    Task<RemoteProject> CreateProjectAsync(RemoteHost host, string path, CancellationToken cancellationToken = default) =>
        throw new IOException("Creating projects is not supported.");

    /// <summary>
    /// What the terminal page starts its control with: a factory whose connection is the session on the host (its
    /// output, this phone's input). The launch spec given to it is ignored, the host already runs the program.
    /// </summary>
    ITerminalConnectionFactory OpenTerminal(RemoteSession session);

    /// <summary>Releases any state held for an open terminal page when the page closes.</summary>
    void CloseTerminal(RemoteSession session) { }

    /// <summary>
    /// True when the terminals are those of another computer: the phone's terminal then only mirrors (it follows the
    /// size and answers nothing to the program; design/12 §4.4).
    /// </summary>
    bool MirrorsTerminals { get; }

    /// <summary>Makes this phone's view decide the session's size (design/12 §6.5).</summary>
    Task ClaimSizeAsync(RemoteSession session, int columns, int rows);

    /// <summary>Gives the size back to the view that had it.</summary>
    Task ReleaseSizeAsync(RemoteSession session);

    /// <summary>
    /// Asks a host that is not paired to allow this phone; both sides then show the same number. The host has to be
    /// accepting new devices (its settings, "Start pairing").
    /// </summary>
    PairingAttempt BeginPairing(RemoteHost host);

    /// <summary>
    /// Pairs with a computer the person names, with or without an account: its address on the local network
    /// (<c>192.168.1.20:7420</c>; both sides then show the same number, as with <see cref="BeginPairing"/>), or a
    /// pairing link copied from the computer (<c>octopus://pair?…</c>; no number, <see cref="PairingAttempt.Code"/>
    /// never completes). The computer joins <see cref="Hosts"/> once it is paired.
    /// </summary>
    PairingAttempt AddHost(string target);

    /// <summary>Reads the computers again (the account's, when signed in) and connects to the paired ones that are there. Cheap enough to call whenever the list is looked at.</summary>
    Task RefreshAsync();

    /// <summary>True while background-to-foreground connection probe and snapshot recovery is in progress.</summary>
    bool IsResuming => false;

    /// <summary>Raised on the UI thread when <see cref="IsResuming"/> starts or completes.</summary>
    event Action<bool>? ResumingChanged
    {
        add { }
        remove { }
    }

    /// <summary>Called when the mobile app moves to the background so periodic network activity and live streams can quiet down.</summary>
    void OnAppBackgrounded() { }

    /// <summary>Called when the mobile app returns to the foreground: probes active links, evicts half-open sockets, and refreshes state.</summary>
    void OnAppForegrounded() { }

    /// <summary>True when the host supports syncing and managing Desktop project groups (<c>project.groups</c>).</summary>
    bool SupportsProjectGroups(RemoteHost host) => false;

    /// <summary>Creates a new project group on the host (syncs with Desktop ProjectGroup).</summary>
    Task<RemoteProjectGroup> CreateProjectGroupAsync(RemoteHost host, string name, string? color = null, bool mergeWorkspaces = false, IReadOnlyList<string>? projectIds = null, CancellationToken cancellationToken = default) =>
        throw new IOException("Project groups are not supported by this host.");

    /// <summary>Updates a project group's name, color, or MergeWorkspaces setting on the host.</summary>
    Task<RemoteProjectGroup> UpdateProjectGroupAsync(RemoteProjectGroup group, string? name = null, string? color = null, bool clearColor = false, bool? mergeWorkspaces = null, CancellationToken cancellationToken = default) =>
        throw new IOException("Project groups are not supported by this host.");

    /// <summary>Dissolves a project group on the host (projects remain ungrouped, never deleted).</summary>
    Task DeleteProjectGroupAsync(RemoteProjectGroup group, CancellationToken cancellationToken = default) =>
        throw new IOException("Project groups are not supported by this host.");

    /// <summary>Moves a project into a group, or out of any group when <paramref name="groupId"/> is null.</summary>
    Task SetProjectGroupAsync(RemoteProject project, string? groupId, CancellationToken cancellationToken = default) =>
        throw new IOException("Project groups are not supported by this host.");
}

public enum PairingOutcome
{
    Paired,
    /// <summary>The person at the host refused, or did not answer in time.</summary>
    Refused,
    /// <summary>The host is not accepting new devices: pairing has to be started there first.</summary>
    NotPairing,
    /// <summary>The host could not be reached.</summary>
    Unreachable,
    Cancelled,
    /// <summary>What was typed is neither an address nor a pairing link (<see cref="IRemoteWorkspace.AddHost"/> only).</summary>
    NotAnAddress,
    /// <summary>A valid pairing link was provided, but it contains no reachable LAN or relay address.</summary>
    PairingLinkNoAddress,
}

/// <summary>How getting into a computer with its access password ended (<see cref="IRemoteWorkspace.AccessWithPasswordAsync"/>).</summary>
public enum AccessOutcome
{
    /// <summary>The computer took the password: it is remembered and connected.</summary>
    Granted,
    /// <summary>Not the computer's access password.</summary>
    WrongPassword,
    /// <summary>Too many wrong passwords from this phone: the computer does not try its passwords for a while (about 15 minutes).</summary>
    Locked,
    /// <summary>Password access is off on that computer, or suspended after too many wrong passwords: pairing still works, or turning it on there.</summary>
    PasswordOff,
    /// <summary>The computer could not be reached (offline, or this phone is not signed in).</summary>
    Unreachable,
    /// <summary>What answered could not prove it knows the password: it may not be the person's computer.</summary>
    NotConfirmed,
    /// <summary>What was typed is not eight letters and digits.</summary>
    NotAPassword,
    Cancelled,
}

/// <summary>One request to pair with a host.</summary>
/// <param name="Code">The six digits both sides show, once the host answered; cancelled when it never did.</param>
/// <param name="Completion">How it ended.</param>
/// <param name="Cancel">Cancels the pairing attempt.</param>
/// <param name="ErrorDetail">Optional diagnostic detail recorded when the attempt could not reach the host.</param>
public sealed record PairingAttempt(Task<string> Code, Task<PairingOutcome> Completion, Action Cancel, Func<string?>? ErrorDetail = null);

public enum SignInOutcome
{
    SignedIn,
    /// <summary>The person refused in the browser.</summary>
    Denied,
    /// <summary>Nobody confirmed in time.</summary>
    Expired,
    Cancelled,
    /// <summary>The service could not be reached any more, or refused this phone.</summary>
    Failed,
}

/// <summary>
/// How this phone is used: signed in to a service (the official one, or one of the person's own), or without an
/// account, with the computers it paired with on the local network (~/code/Octopus/design/mobile/01-登录与连接方式.md).
/// </summary>
public interface IAccountSession
{
    bool IsSignedIn { get; }
    /// <summary>
    /// Not signed in, and the person chose to use the app without an account: the host list shows the computers this
    /// phone paired with itself. Kept across restarts until <see cref="LeaveLocalOnly"/>.
    /// </summary>
    bool IsLocalOnly { get; }
    /// <summary>The account's name; null while signed out.</summary>
    string? Name { get; }
    /// <summary>The official service's address; null when this build has none (then "Sign in" asks for an address).</summary>
    string? OfficialServer { get; }
    /// <summary>The address of a service of the person's own, as they last typed it (what that page starts with); empty when they typed none.</summary>
    string Server { get; }
    /// <summary>The service ended this phone's sign-in (removed from the account, signed out everywhere) and nobody signed in since.</summary>
    bool SignInEnded { get; }
    /// <summary>Raised when <see cref="IsSignedIn"/> or <see cref="IsLocalOnly"/> changed, on any thread.</summary>
    event EventHandler? Changed;
    /// <summary>
    /// Starts the device-code sign-in at a service: the app shows the code, the person confirms it in the browser.
    /// Throws <see cref="IOException"/> (with words for the person) when the service cannot be reached or is none.
    /// </summary>
    Task<SignInChallenge> BeginSignInAsync(string server, CancellationToken cancellationToken = default);
    /// <summary>
    /// Signs this phone in directly at a self-hosted relay using its Access Key (no browser step).
    /// Throws <see cref="IOException"/> when the address is invalid, the service cannot be reached, or the key is rejected.
    /// </summary>
    Task SignInWithKeyAsync(string server, string accessKey, CancellationToken cancellationToken = default) =>
        throw new IOException("Access-key sign-in is not supported.");
    void SignOut();
    /// <summary>From now on the app is used without an account (until someone signs in, and again after they sign out).</summary>
    void UseLocalOnly();
    /// <summary>Back to choosing how to connect. The computers this phone is paired with stay paired.</summary>
    void LeaveLocalOnly();
}

/// <param name="UserCode">The code to confirm in the browser.</param>
/// <param name="Completion">How it ended.</param>
public sealed record SignInChallenge(string UserCode, Uri VerificationUri, Task<SignInOutcome> Completion, Action Cancel);
