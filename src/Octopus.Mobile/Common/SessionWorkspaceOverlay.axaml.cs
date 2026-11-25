using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ArcPage = TKCode.Arc.Mobile.Page;

namespace Octopus.Mobile.Common;

/// <summary>
/// Scheme C anchored top-right popover menu plus the Branches and Sessions sheets, shared by
/// <c>ConversationPage</c> and <c>TerminalPage</c>.
/// </summary>
public partial class SessionWorkspaceOverlay : UserControl
{
    private const double TopBarOffset = 54;
    private ArcPage? _page;

    public SessionWorkspaceOverlay()
    {
        InitializeComponent();
    }

    public bool IsPopoverOpen => PopoverOverlay.IsVisible;
    public bool IsBranchesOpen => BranchesSheet.IsOpen;
    public bool IsSessionsOpen => SessionsSheet.IsOpen;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _page = this.FindAncestorOfType<ArcPage>();
        if (_page is not null)
        {
            _page.PropertyChanged += OnPagePropertyChanged;
            UpdatePopoverOffset();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_page is not null)
        {
            _page.PropertyChanged -= OnPagePropertyChanged;
            _page = null;
        }
    }

    private void OnPagePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ArcPage.SafeAreaProperty) UpdatePopoverOffset();
    }

    private void UpdatePopoverOffset()
    {
        var topInset = _page?.SafeArea.Top ?? 0;
        PopoverHost.Padding = new Thickness(0, topInset + TopBarOffset, 10, 0);
    }
}
