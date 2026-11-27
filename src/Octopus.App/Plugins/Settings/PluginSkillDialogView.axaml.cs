using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Octopus.App.Plugins.Settings;

/// <summary>Modal body for installing/overwriting the official <c>octopus-plugin</c> authoring Skill across AI coding agents.</summary>
public partial class PluginSkillDialogView : UserControl
{
    public PluginSkillDialogView()
    {
        InitializeComponent();
    }

    private void OnSkillCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // Let the CheckBox itself handle its own click without double-toggling.
        if (e.Source is Avalonia.Visual visual && visual.FindAncestorOfType<CheckBox>(includeSelf: true) is not null)
        {
            return;
        }
        if (sender is Control { DataContext: SkillTargetRowViewModel row })
        {
            row.IsSelected = !row.IsSelected;
            e.Handled = true;
        }
    }
}
