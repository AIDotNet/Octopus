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
