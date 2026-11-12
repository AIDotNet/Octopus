using Avalonia;
using Avalonia.Controls;
using TKCode.Arc;

namespace Octopus.App.Updates;

/// <summary>
/// The update card. Toasts use the same corner: while the card shows, the window's toast stack is lifted above it so
/// neither covers the other.
/// </summary>
public partial class UpdateCardView : UserControl
{
    private const double Gap = 12;

    public UpdateCardView()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty || e.Property == BoundsProperty) LiftToasts();
        };
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        SetToastOffset(0);
    }

    private void LiftToasts() => SetToastOffset(IsVisible && Bounds.Height > 0 ? Bounds.Height + Gap : 0);

    private void SetToastOffset(double offset)
    {
        if (TopLevel.GetTopLevel(this) is null) return;
        var stack = ToastHost.GetStack(this);
        if (stack is null) return;
        var margin = stack.Margin;
        if (margin.Bottom != offset) stack.Margin = new Thickness(margin.Left, margin.Top, margin.Right, offset);
    }
}
