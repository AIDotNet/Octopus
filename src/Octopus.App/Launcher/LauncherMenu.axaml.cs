using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Octopus.App.Launcher;

public partial class LauncherMenu : UserControl
{
    public LauncherMenu() => InitializeComponent();

    /// <summary>
    /// True when the menu opens from the right edge: the second panel then sits on the left, so the first level stays
    /// where it is when the panel appears.
    /// </summary>
    public bool PanelOnLeft
    {
        get;
        set
        {
            field = value;
            Grid.SetColumn(FirstLevel, value ? 1 : 0);
            Grid.SetColumn(SubPanel, value ? 0 : 1);
            SubPanel.Margin = value ? new Thickness(0, 0, 4, 0) : new Thickness(4, 0, 0, 0);
            SubPanel.BorderThickness = value ? new Thickness(0, 0, 1, 0) : new Thickness(1, 0, 0, 0);
        }
    }

    /// <summary>
    /// The tallest the second panel gets (model list, effort, fast switch, prompt, launch row). The side the menu
    /// opens on is chosen for this height up front, so hovering an agent never makes the popup change sides.
    /// </summary>
    public const double ExpandedPanelHeight = 470;

    /// <summary>
    /// True when the menu opens above its anchor: its bottom edge is the fixed one then, so both levels hang from
    /// the bottom and the first level stays put while the second panel appears.
    /// </summary>
    public bool OpensUpward
    {
        get;
        set
        {
            field = value;
            var alignment = value ? Avalonia.Layout.VerticalAlignment.Bottom : Avalonia.Layout.VerticalAlignment.Top;
            FirstLevel.VerticalAlignment = alignment;
            SubPanel.VerticalAlignment = alignment;
        }
    }

    /// <summary>Height limit of the second panel: the room on the side the menu opened on.</summary>
    public double PanelMaxHeight
    {
        get => SubPanel.MaxHeight;
        set => SubPanel.MaxHeight = value;
    }

    private LauncherViewModel? Model => DataContext as LauncherViewModel;

    /// <summary>Hovering an agent row opens its panel; hovering a plain row closes it.</summary>
    private void OnFirstLevelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (Model is null || e.Source is not Visual source)
        {
            return;
        }
        for (Visual? visual = source; visual is not null && !ReferenceEquals(visual, FirstLevel); visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: LauncherAgentViewModel agent })
            {
                Model.Expand(agent);
                return;
            }
            if (visual is Control control && control.Classes.Contains("plain"))
            {
                Model.Expand(null);
                return;
            }
        }
    }
}
