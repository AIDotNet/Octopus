using Avalonia;
using Avalonia.Controls;

namespace Octopus.Mobile.Shell;

/// <summary>
/// The navbar's row: leading at the start, trailing at the end, the title between them. The title is centred on the
/// bar while it fits between two slots as wide as the wider side; a longer title moves into the gap between the two
/// sides and is trimmed there, so it never covers the back button or the actions (TKCode.Arc.Mobile's own row, a
/// <c>*,Auto,*</c> grid, gives a long title the whole width and leaves the side columns none).
/// </summary>
/// <remarks>Children in order: leading, title, trailing.</remarks>
public sealed class NavbarBarPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 3) return base.MeasureOverride(availableSize);
        var (leading, title, trailing) = (Children[0], Children[1], Children[2]);

        var side = new Size(double.PositiveInfinity, availableSize.Height);
        leading.Measure(side);
        trailing.Measure(side);
        var leadingWidth = leading.DesiredSize.Width;
        var trailingWidth = trailing.DesiredSize.Width;

        var gap = double.IsInfinity(availableSize.Width)
            ? double.PositiveInfinity
            : Math.Max(0, availableSize.Width - leadingWidth - trailingWidth);
        title.Measure(new Size(gap, availableSize.Height));

        var height = Math.Max(title.DesiredSize.Height, Math.Max(leading.DesiredSize.Height, trailing.DesiredSize.Height));
        var width = double.IsInfinity(availableSize.Width)
            ? leadingWidth + title.DesiredSize.Width + trailingWidth
            : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 3) return base.ArrangeOverride(finalSize);
        var (leading, title, trailing) = (Children[0], Children[1], Children[2]);

        var leadingWidth = Math.Min(leading.DesiredSize.Width, finalSize.Width);
        var trailingWidth = Math.Min(trailing.DesiredSize.Width, Math.Max(0, finalSize.Width - leadingWidth));
        leading.Arrange(new Rect(0, 0, leadingWidth, finalSize.Height));
        trailing.Arrange(new Rect(finalSize.Width - trailingWidth, 0, trailingWidth, finalSize.Height));

        var gapStart = leadingWidth;
        var gap = Math.Max(0, finalSize.Width - leadingWidth - trailingWidth);
        var titleWidth = Math.Min(title.DesiredSize.Width, gap);

        // Centred on the bar when that keeps it inside the gap; otherwise as close to the centre as the gap allows.
        var x = (finalSize.Width - titleWidth) / 2;
        x = Math.Clamp(x, gapStart, gapStart + gap - titleWidth);
        title.Arrange(new Rect(x, 0, titleWidth, finalSize.Height));
        return finalSize;
    }
}
