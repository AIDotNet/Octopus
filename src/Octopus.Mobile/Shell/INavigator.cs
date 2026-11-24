using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Shell;

/// <summary>
/// Where the view models send the person next. The layout is a plain hierarchy (design/ui/移动端布局方案.html, A):
/// hosts, then one host's projects and sessions, then one session's terminal. Before there are hosts to show, the
/// first page is the welcome page, with the pages that sign in or add a computer on top of it
/// (~/code/Octopus/design/mobile/01-登录与连接方式.md).
/// </summary>
public interface INavigator
{
    void ShowHost(RemoteHost host);
    /// <summary>A session's terminal; a session that runs as a GUI conversation on the host opens its details instead.</summary>
    void ShowSession(RemoteSession session);
    /// <summary>Everything about a session and what can be done to it.</summary>
    void ShowSessionDetails(RemoteSession session);
    /// <summary>Picks a folder of a host and makes it a project there; <paramref name="created"/> runs after the page left.</summary>
    void ShowFolderPicker(RemoteHost host, Action<RemoteProject>? created = null);
    void ShowPairing(RemoteHost host);
    /// <summary>The number of a pairing that is already under way: a computer the person named by its address.</summary>
    void ShowPairing(string hostName, PairingAttempt attempt);
    /// <summary>The page that takes a computer's address or a pairing link.</summary>
    void ShowAddHost();
    /// <summary>Signing in from the host list of a phone used without an account.</summary>
    void ShowSignIn();
    /// <summary>The page that takes the address of a service of the person's own.</summary>
    void ShowCustomServer();
    /// <summary>Back to the first page, whatever is on top of it.</summary>
    void ShowFirst();
    /// <summary>Leaves the page on top.</summary>
    void Back();
}

/// <summary>A page that holds something to let go of when it leaves the stack for good (not when another page covers it).</summary>
public interface IClosablePage
{
    void OnClosed();
}
