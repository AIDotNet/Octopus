using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.Mobile.Account;
using Octopus.Mobile.Conversations;
using Octopus.Mobile.Files;
using Octopus.Mobile.Hosts;
using Octopus.Mobile.Sessions;
using Octopus.Mobile.Terminals;
using Octopus.Mobile.Updates;
using Octopus.Mobile.Workspace;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Shell;

/// <summary>
/// The whole app in one control: a stack of pages that starts at the host list while there are hosts to show (signed
/// in, or used without an account), and at the welcome page otherwise. It is the navigator of every view model.
/// </summary>
public sealed class MobileShell : ContentControl, INavigator
{
    private readonly IRemoteWorkspace _workspace;
    private readonly IAccountSession _account;
    private readonly Action<Uri> _openBrowser;
    private readonly MobileUpdater _updater;
    // The pages above the first one, bottom to top: the stack control only tells that a page left, not which.
    private readonly List<Control> _pages = [];
    private NavigationStack? _stack;

    protected override Type StyleKeyOverride => typeof(ContentControl);

    public MobileShell(IRemoteWorkspace workspace, IAccountSession account, Action<Uri>? openBrowser = null, MobileUpdater? updater = null)
    {
        _workspace = workspace;
        _account = account;
        _openBrowser = openBrowser ?? (_ => { });
        _updater = updater ?? MobileUpdater.Unsupported;
        // Each Page keeps clear of the status bar, the gesture bar and the keyboard itself (Page.SafeArea). Avalonia
        // would also pad the top level's content by the same safe area, and every page would sit twice as far from the
        // edges: on Android 15+ (edge to edge) about a status bar's height of empty space above every header.
        TopLevel.SetAutoSafeAreaPadding(this, false);
        account.Changed += OnAccountChanged;
        Show();
    }

    private TopLevel? _topLevel;

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.ScalingChanged += OnScalingChanged;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_topLevel is not null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
    }

    /// <summary>
    /// Android reports the safe area in device pixels divided by the top level's scaling, and the first page reads it
    /// while that scaling is still 1: the status and gesture bars' pixels (128 and 63 on a 2.625 phone) become as many
    /// points, a wide empty band above the header and under the tab bar. A change of scaling raises no safe-area
    /// change, so the pages showing then read it again here (a page covered by another reads it when it is back).
    /// </summary>
    private void OnScalingChanged(object? sender, EventArgs e)
    {
        if (_topLevel?.InsetsManager is not { } insets) return;
        foreach (var page in this.GetVisualDescendants().OfType<TKCode.Arc.Mobile.Page>())
        {
            if (page.UsePlatformSafeArea) page.SafeArea = insets.SafeAreaPadding;
        }
    }

    /// <summary>The stack of pages.</summary>
    public NavigationStack? Stack => _stack;

    /// <summary>The page on top of the stack.</summary>
    public Control? Top => _stack?.Top;

    /// <summary>True while the first page is the host list rather than the welcome page.</summary>
    private bool ShowsHosts => _account.IsSignedIn || _account.IsLocalOnly;

    public void ShowHost(RemoteHost host) => Push(new HostPage(new HostViewModel(host, _workspace, this)));

    public void ShowSession(RemoteSession session)
    {
        // A GUI session opens its conversation when the host speaks acp.gui (G-A), or its details with an update note (G-0).
        if (session.IsGui)
        {
            if (_workspace.SupportsConversations(session.Project.Host)) ShowConversation(session);
            else ShowSessionDetails(session);
            return;
        }
        Push(new TerminalPage(new TerminalViewModel(session, _workspace, this)));
    }

    public void ShowConversation(RemoteSession session) => Push(new ConversationPage(new ConversationViewModel(session, _workspace, this)));

    public void ShowSessionDetails(RemoteSession session) => Push(new SessionDetailPage(new SessionDetailViewModel(session, _workspace, this)));

    public void ShowFolderPicker(RemoteHost host, Action<RemoteProject>? created = null) =>
        Push(new FolderPickerPage(new FolderPickerViewModel(host, _workspace, this, created)));

    // Files, changes and diffs (Files/).
    public void ShowFiles(RemoteProject project, string? folder = null) =>
        Push(new FilesPage(new FilesViewModel(project, folder ?? project.Path, _workspace, this)));

    public void ShowFile(RemoteProject project, string path, string? location = null) =>
        Push(new FileViewerPage(new FileViewerViewModel(project, path, location, _workspace, this)));

    public void ShowChanges(RemoteProject project, string? workspacePath = null) =>
        Push(new ChangesPage(new ChangesViewModel(project, workspacePath, _workspace, this)));

    public void ShowDiff(RemoteProject project, string? workspacePath, RemoteChange change) =>
        Push(new DiffPage(new DiffViewModel(project, workspacePath, change, _workspace, this)));

    public void ShowPairing(RemoteHost host) => Push(new PairingPage(new PairingViewModel(host, _workspace, this)));

    public void ShowAccessPassword(RemoteHost host) => Push(new AccessPasswordPage(new AccessPasswordViewModel(host, _workspace, this)));

    public void ShowPairing(string hostName, PairingAttempt attempt) => Push(new PairingPage(new PairingViewModel(hostName, attempt, this)));

    public void ShowAddHost() => Push(new AddHostPage(new AddHostViewModel(_workspace, _account, this)));

    public void ShowSignIn()
    {
        // With an official service: the welcome page's "Sign in", on top of the list. Without one there is only the address to ask for.
        if (_account.OfficialServer is null) ShowCustomServer();
        else Push(new WelcomePage(new WelcomeViewModel(_account, _workspace, this, _openBrowser, isOnTop: true)));
    }

    public void ShowCustomServer() => Push(new CustomServerPage(new CustomServerViewModel(_account, this, _openBrowser)));

    public void ShowFirst() => _stack?.PopToRoot();

    public void Back() => _stack?.Pop();

    private void Push(Control page)
    {
        if (_stack is null) return;
        _pages.Add(page);
        _stack.Push(page);
    }

    // Signing in or out, and choosing or leaving the use without an account, replace everything: no page of one
    // survives into the other.
    private void Show()
    {
        CloseAll();
        Control first = ShowsHosts
            ? new HostsPage(new HostsViewModel(_workspace, _account, this, _openBrowser, _updater))
            : new WelcomePage(new WelcomeViewModel(_account, _workspace, this, _openBrowser));
        _stack = new NavigationStack { Root = first };
        _stack.Navigated += OnNavigated;
        Content = _stack;
    }

    private void CloseAll()
    {
        foreach (var page in _pages) (page as IClosablePage)?.OnClosed();
        _pages.Clear();
        (_stack?.Root as IClosablePage)?.OnClosed();
        if (_stack is not null) _stack.Navigated -= OnNavigated;
        _stack = null;
    }

    // A pop (the back button, the back swipe or Back()) lets go of what the page held.
    private void OnNavigated(object? sender, TKCode.Arc.Mobile.NavigationEventArgs e)
    {
        if (e.Kind == NavigationKind.Pop && _stack is not null)
        {
            while (_pages.Count > _stack.Depth - 1)
            {
                var page = _pages[^1];
                _pages.RemoveAt(_pages.Count - 1);
                (page as IClosablePage)?.OnClosed();
            }
        }
        if (_stack?.Top is ConversationPage conversation)
        {
            conversation.ScrollToBottomOnEntry();
        }
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) Show();
        else Dispatcher.UIThread.Post(Show);
    }
}
