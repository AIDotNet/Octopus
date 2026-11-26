using Avalonia;
using Avalonia.Threading;
using Octopus.Mobile.Shell;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Hosts;

/// <summary>Scheme A root dashboard page: Activity inbox, Computers, and Me tabs.</summary>
public partial class HostsPage : Page, IClosablePage
{
    /// <summary>How often the list is read again while the page is showing: a computer that came online shows up by itself.</summary>
    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(10);

    private readonly HostsViewModel _viewModel;
    private readonly DispatcherTimer _refresh;
    private bool _attached;

    public HostsPage(HostsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        _refresh = new DispatcherTimer { Interval = Refresh };
        _refresh.Tick += (_, _) =>
        {
            if (!MobileHost.IsBackgrounded) _ = viewModel.RefreshAsync();
        };
    }

    // Also when coming back to the dashboard from a host or terminal: what changed meanwhile is shown at once.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        MobileHost.Backgrounded += OnBackgrounded;
        MobileHost.Foregrounded += OnForegrounded;
        if (!MobileHost.IsBackgrounded)
        {
            _ = _viewModel.RefreshAsync();
            _refresh.Start();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        MobileHost.Backgrounded -= OnBackgrounded;
        MobileHost.Foregrounded -= OnForegrounded;
        _refresh.Stop();
    }

    public void OnClosed()
    {
        _attached = false;
        MobileHost.Backgrounded -= OnBackgrounded;
        MobileHost.Foregrounded -= OnForegrounded;
        _refresh.Stop();
        _viewModel.Dispose();
    }

    private void OnBackgrounded() => Dispatcher.UIThread.Post(_refresh.Stop);

    private void OnForegrounded() => Dispatcher.UIThread.Post(() =>
    {
        if (!_attached || MobileHost.IsBackgrounded) return;
        _ = _viewModel.RefreshAsync();
        _refresh.Start();
    });
}
