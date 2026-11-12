using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using TKCode.Arc;

namespace Octopus.App.Workspace;

public partial class WorkspaceView : UserControl
{
    public static readonly StyledProperty<double> LeftInsetProperty =
        AvaloniaProperty.Register<WorkspaceView, double>(nameof(LeftInset));

    public static readonly StyledProperty<double> RightInsetProperty =
        AvaloniaProperty.Register<WorkspaceView, double>(nameof(RightInset));

    private const double StripSide = 4;
    /// <summary>Space between the last tab and the "+", and between the "+" and whatever follows it.</summary>
    private const double AddGap = 2;

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Grid, StripAddButton> _addButtons = [];

    public WorkspaceView()
    {
        InitializeComponent();
        LayoutUpdated += (_, _) => ApplyStrips();
        LeftInsetProperty.Changed.AddClassHandler<WorkspaceView>((view, _) => view.ApplyStrips());
        RightInsetProperty.Changed.AddClassHandler<WorkspaceView>((view, _) => view.ApplyStrips());
    }

    /// <summary>Room the tab strip of the group at the top left keeps free (the collapsed sidebar's lead).</summary>
    public double LeftInset { get => GetValue(LeftInsetProperty); set => SetValue(LeftInsetProperty, value); }

    /// <summary>Room the tab strip of the group at the top right keeps free (the split button).</summary>
    public double RightInset { get => GetValue(RightInsetProperty); set => SetValue(RightInsetProperty, value); }

    /// <summary>
    /// The dock layout has no slot for content in its tab strips (TKCode.Arc.Dock 1.1.0-preview.4: no trailing-content
    /// property, no replaceable tab strip template, the group view is internal), so this view reaches into the template
    /// of every group's strip row by its part names:
    /// <list type="bullet">
    /// <item>The strips that touch the top-left and top-right corners of the workspace get a margin for the overlays of the shell.</item>
    /// <item>Every strip gets a "+" in its row, right after the last tab (or at the end of the visible tab area when the tabs
    /// overflow), and the tab strip's own right margin keeps that room free so the "+" never covers a tab, the overflow
    /// chevron or the "..." button.</item>
    /// </list>
    /// Every value is only written when it changed, so the layout pass this triggers settles after one round.
    /// </summary>
    private void ApplyStrips()
    {
        foreach (var group in GroupViews(this))
        {
            if (FindStripRow(group) is not { } row || group.TranslatePoint(default, this) is not { } origin)
            {
                continue;
            }
            var atTop = origin.Y < 1.5;
            var left = atTop && origin.X < 1.5 ? LeftInset : 0;
            var right = atTop && origin.X + group.Bounds.Width > Bounds.Width - 1.5 ? RightInset : 0;
            var margin = new Thickness(StripSide + left, 0, StripSide + right, 0);
            if (row.Margin != margin)
            {
                row.Margin = margin;
            }
            PlaceAddButton(group, row);
        }
    }

    private void PlaceAddButton(Control group, Grid row)
    {
        var strip = row.Children.FirstOrDefault(c => c.Name == "PART_Strip");
        if (strip is null)
        {
            return;
        }
        if (!_addButtons.TryGetValue(row, out var button))
        {
            button = new StripAddButton
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                ActivateGroup = () => (DataContext as WorkspaceViewModel)?.SetNewTabGroup(GroupOf(group)),
            };
            Grid.SetColumn(button, 0);
            row.Children.Add(button);
            _addButtons.Add(row, button);
        }
        // The button's own width, read once while it still has no margin: with the margin that places it, the available
        // room can be smaller than the button and its desired size no longer says how wide it is.
        if (button.Tag is not double width)
        {
            if (button.DesiredSize.Width <= 0)
            {
                // Not measured yet: the next layout pass comes right after this child was added.
                return;
            }
            width = button.DesiredSize.Width;
            button.Tag = width;
        }
        var reserve = width + AddGap;
        if (strip.Margin.Right != reserve)
        {
            strip.Margin = new Thickness(0, 0, reserve, 0);
        }
        var lastRight = 0.0;
        foreach (var tab in strip.GetVisualChildren().OfType<Control>().Where(c => c.GetType().Name == "DockTab"))
        {
            lastRight = Math.Max(lastRight, tab.Bounds.Right);
        }
        // Follows the last tab; at the end of the visible tab area when the tabs do not fit (the strip clips them there).
        var x = strip.Bounds.X + Math.Min(lastRight, strip.Bounds.Width) + AddGap;
        var placed = new Thickness(Math.Round(x), 0, 0, 0);
        if (button.Margin != placed)
        {
            button.Margin = placed;
        }
    }

    /// <summary>The group a group view shows: the group of the pane in its content.</summary>
    private static DockGroup? GroupOf(Control groupView)
    {
        var content = groupView.GetVisualDescendants().OfType<Decorator>().FirstOrDefault(d => d.Name == "PART_Content");
        var pane = content?.Child as DockPane ?? content?.GetVisualDescendants().OfType<DockPane>().FirstOrDefault();
        return pane?.Group;
    }

    /// <summary>The group views in the tree; their content (the terminals) is not searched.</summary>
    private static IEnumerable<Control> GroupViews(Visual root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is Control { } control && control.GetType().Name == "DockGroupView")
            {
                yield return control;
            }
            else
            {
                foreach (var found in GroupViews(child))
                {
                    yield return found;
                }
            }
        }
    }

    private static Grid? FindStripRow(Visual groupView)
    {
        foreach (var child in groupView.GetVisualChildren())
        {
            if (child is Grid { Name: "PART_StripRow" } row)
            {
                return row;
            }
            if (child is Decorator { Name: "PART_Content" })
            {
                continue;
            }
            if (FindStripRow(child) is { } found)
            {
                return found;
            }
        }
        return null;
    }
}
