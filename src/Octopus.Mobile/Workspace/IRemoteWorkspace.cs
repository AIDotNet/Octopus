using System.Collections.ObjectModel;
using Octopus.Terminal;

namespace Octopus.Mobile.Workspace;

/// <summary>
/// The hosts of the account and what runs on them: the one seam between the phone's pages and the remote protocol
/// (design/12-远程操作.md §6). The pages only use this; <see cref="Demo.DemoWorkspace"/> stands in until the protocol
/// client exists. Every collection and property changes on the UI thread.
/// </summary>
public interface IRemoteWorkspace
{
    /// <summary>Paired hosts, and the account's other computers that are not paired yet.</summary>
    ObservableCollection<RemoteHost> Hosts { get; }

    /// <summary>Starts a terminal or agent in a project on its host; the new session is also added to the project.</summary>
    Task<RemoteSession> CreateSessionAsync(RemoteProject project, NewSessionRequest request, CancellationToken cancellationToken = default);

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

    /// <summary>Reads the account's computers again and connects to the paired ones that are there. Cheap enough to call whenever the list is looked at.</summary>
    Task RefreshAsync();
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
}

/// <summary>One request to pair with a host.</summary>
/// <param name="Code">The six digits both sides show, once the host answered; cancelled when it never did.</param>
/// <param name="Completion">How it ended.</param>
public sealed record PairingAttempt(Task<string> Code, Task<PairingOutcome> Completion, Action Cancel);

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

/// <summary>The signed-in account. The phone app is only usable signed in (design/12 §9.3).</summary>
public interface IAccountSession
{
    bool IsSignedIn { get; }
    /// <summary>The account's name; null while signed out.</summary>
    string? Name { get; }
    /// <summary>The service's address as last used (what the sign-in page starts with); empty when there is none yet.</summary>
    string Server { get; }
    /// <summary>The service ended this phone's sign-in (removed from the account, signed out everywhere) and nobody signed in since.</summary>
    bool SignInEnded { get; }
    /// <summary>Raised when <see cref="IsSignedIn"/> changed, on any thread.</summary>
    event EventHandler? Changed;
    /// <summary>
    /// Starts the device-code sign-in at a service: the app shows the code, the person confirms it in the browser.
    /// Throws <see cref="IOException"/> (with words for the person) when the service cannot be reached or is none.
    /// </summary>
    Task<SignInChallenge> BeginSignInAsync(string server, CancellationToken cancellationToken = default);
    void SignOut();
}

/// <param name="UserCode">The code to confirm in the browser.</param>
/// <param name="Completion">How it ended.</param>
public sealed record SignInChallenge(string UserCode, Uri VerificationUri, Task<SignInOutcome> Completion, Action Cancel);
