using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Shell;

/// <summary>
/// Where the view models send the person next. The layout is a plain hierarchy (design/ui/移动端布局方案.html, A):
/// hosts, then one host's projects and sessions, then one session's terminal.
/// </summary>
public interface INavigator
{
    void ShowHost(RemoteHost host);
    void ShowSession(RemoteSession session);
    void ShowPairing(RemoteHost host);
    /// <summary>Leaves the page on top.</summary>
    void Back();
}

/// <summary>A page that holds something to let go of when it leaves the stack for good (not when another page covers it).</summary>
public interface IClosablePage
{
    void OnClosed();
}
