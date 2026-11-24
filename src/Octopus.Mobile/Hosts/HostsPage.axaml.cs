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

    public HostsPage(HostsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        _refresh = new DispatcherTimer { Interval = Refresh };
        _refresh.Tick += (_, _) => _ = viewModel.RefreshAsync();
    }

    // Also when coming back to the dashboard from a host or terminal: what changed meanwhile is shown at once.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ = _viewModel.RefreshAsync();
        _refresh.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _refresh.Stop();
    }

    public void OnClosed()
    {
        _refresh.Stop();
        _viewModel.Dispose();
    }
}
