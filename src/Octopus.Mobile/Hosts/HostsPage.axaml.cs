using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Octopus.Mobile.Shell;
using Page = TKCode.Arc.Mobile.Page;

namespace Octopus.Mobile.Hosts;

/// <summary>Root dashboard page: Activity inbox, Bots, Computers, and Me tabs with lazy single-tab visual mounting.</summary>
public partial class HostsPage : Page, IClosablePage
{
    /// <summary>How often the list is read again while the page is showing: a computer that came online shows up by itself.</summary>
    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(10);

    private readonly HostsViewModel _viewModel;
    private readonly DispatcherTimer _refresh;
    private readonly Control[] _tabViews;
    private bool _attached;

    public HostsPage(HostsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        // Detach the 4 tab trees from TabsStagingHost (preserving XAML NameScope registration) and mount ONLY
        // the active tab into ActiveTabSlot so inactive tabs incur zero layout/render cost on background resume.
        _tabViews = [ActivityTabContent, BotsTabHost, HostsTabContent, AccountTabContent];
        TabsStagingHost.Children.Clear();
        DataContext = viewModel;
        MountActiveTab();
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        _refresh = new DispatcherTimer { Interval = Refresh };
        _refresh.Tick += (_, _) =>
        {
            if (!MobileHost.IsBackgrounded) _ = viewModel.RefreshAsync();
        };
    }

    /// <summary>Currently mounted root tab view inside <see cref="ActiveTabSlot"/>, exposed for lazy-mounting tests.</summary>
    internal Control? ActiveTabView => ActiveTabSlot.Content as Control;

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
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _refresh.Stop();
        ActiveTabSlot.Content = null;
        _viewModel.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HostsViewModel.SelectedTab))
        {
            MountActiveTab();
        }
    }

    private void MountActiveTab()
    {
        var idx = Math.Clamp(_viewModel.SelectedTab, 0, _tabViews.Length - 1);
        for (var i = 0; i < _tabViews.Length; i++)
        {
            _tabViews[i].IsVisible = i == idx;
        }
        var next = _tabViews[idx];
        if (!ReferenceEquals(ActiveTabSlot.Content, next))
        {
            ActiveTabSlot.Content = next;
        }
    }

    private void OnBackgrounded() => Dispatcher.UIThread.Post(_refresh.Stop);

    private void OnForegrounded() => Dispatcher.UIThread.Post(() =>
    {
        if (!_attached || MobileHost.IsBackgrounded) return;
        _ = _viewModel.RefreshAsync();
        _refresh.Start();
    });
}
