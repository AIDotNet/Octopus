using Avalonia;
using Avalonia.Threading;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Hosts;

/// <summary>The first page: the account's computers (design/ui/移动端布局方案.html, A ①).</summary>
public partial class HostsPage : Page
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

    // Also when coming back to the list from a host: what changed meanwhile is shown at once.
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
}
