using Avalonia.Controls;
using Avalonia.Threading;
using Octopus.Mobile.Account;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Terminals;
using Octopus.Mobile.Workspace;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Shell;

/// <summary>
/// The whole app in one control: the sign-in page while signed out, otherwise a stack of pages that starts at the host
/// list. It is the navigator of every view model.
/// </summary>
public sealed class MobileShell : ContentControl, INavigator
{
    private readonly IRemoteWorkspace _workspace;
    private readonly IAccountSession _account;
    private readonly Action<Uri> _openBrowser;
    // The pages above the first one, bottom to top: the stack control only tells that a page left, not which.
    private readonly List<Control> _pages = [];
    private NavigationStack? _stack;

    protected override Type StyleKeyOverride => typeof(ContentControl);

    public MobileShell(IRemoteWorkspace workspace, IAccountSession account, Action<Uri>? openBrowser = null)
    {
        _workspace = workspace;
        _account = account;
        _openBrowser = openBrowser ?? (_ => { });
        account.Changed += OnAccountChanged;
        Show();
    }

    /// <summary>The stack of pages; null while signed out.</summary>
    public NavigationStack? Stack => _stack;

    /// <summary>The page on top: of the stack, or the sign-in page.</summary>
    public Control? Top => _stack?.Top ?? Content as Control;

    public void ShowHost(RemoteHost host) => Push(new HostPage(new HostViewModel(host, _workspace, this)));

    public void ShowSession(RemoteSession session) => Push(new TerminalPage(new TerminalViewModel(session, _workspace, this)));

    public void ShowPairing(RemoteHost host) => Push(new PairingPage(new PairingViewModel(host, _workspace, this)));

    public void Back() => _stack?.Pop();

    private void Push(Control page)
    {
        if (_stack is null) return;
        _pages.Add(page);
        _stack.Push(page);
    }

    // Signing in or out replaces everything: no page of one account survives into the other.
    private void Show()
    {
        CloseAll();
        if (_account.IsSignedIn)
        {
            _stack = new NavigationStack { Root = new HostsPage(new HostsViewModel(_workspace, _account, this)) };
            _stack.Navigated += OnNavigated;
            Content = _stack;
        }
        else
        {
            Content = new SignInPage(new SignInViewModel(_account, _openBrowser));
        }
    }

    private void CloseAll()
    {
        foreach (var page in _pages) (page as IClosablePage)?.OnClosed();
        _pages.Clear();
        (Content as IClosablePage)?.OnClosed();
        if (_stack is not null) _stack.Navigated -= OnNavigated;
        _stack = null;
    }

    // A pop (the back button, the back swipe or Back()) lets go of what the page held.
    private void OnNavigated(object? sender, TKCode.Arc.Mobile.NavigationEventArgs e)
    {
        if (e.Kind != NavigationKind.Pop || _stack is null) return;
        while (_pages.Count > _stack.Depth - 1)
        {
            var page = _pages[^1];
            _pages.RemoveAt(_pages.Count - 1);
            (page as IClosablePage)?.OnClosed();
        }
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) Show();
        else Dispatcher.UIThread.Post(Show);
    }
}
