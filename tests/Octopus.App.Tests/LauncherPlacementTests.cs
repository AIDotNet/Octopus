using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Octopus.App.Launcher;
using Octopus.App.Shell;
using TKCode.Arc;
using Xunit;

namespace Octopus.App.Tests;

/// <summary>
/// The launcher menu picks its side for the height it has once an agent's panel is showing. Picked for the first
/// level alone, the popup fits below, stops fitting when the panel appears, and jumps above the anchor mid-hover.
/// </summary>
public class LauncherPlacementTests
{
    [AvaloniaTheory]
    [InlineData(40, false)]
    [InlineData(380, true)]
    public void The_side_is_chosen_for_the_expanded_height(double anchorTop, bool upward)
    {
        var window = new MainWindow { Width = 1260, Height = 728 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var shell = window.GetVisualDescendants().OfType<ShellView>().Single();
            var root = shell.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "Root");
            var anchor = new Border
            {
                Width = 80, Height = 30,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(300, anchorTop, 0, 0),
            };
            root.Children.Add(anchor);
            Dispatcher.UIThread.RunJobs();

            LauncherRequest.Raise(anchor);
            Dispatcher.UIThread.RunJobs();

            var popover = shell.GetVisualDescendants().OfType<Popover>().First(p => p.Name == "LauncherPopover");
            var menu = (LauncherMenu)popover.Content!;
            Assert.Equal(upward ? PopoverPlacement.Top : PopoverPlacement.Bottom, popover.Placement);
            Assert.Equal(upward, menu.OpensUpward);

            // The panel never asks for more than the chosen side has, so the popup has no reason to change sides.
            var room = upward ? anchorTop : shell.Bounds.Height - anchorTop - anchor.Bounds.Height;
            Assert.True(menu.PanelMaxHeight <= Math.Max(160, room), $"{menu.PanelMaxHeight} > {room}");
            popover.Close();
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
