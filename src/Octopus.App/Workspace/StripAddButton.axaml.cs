using Avalonia.Controls;
using Avalonia.Interactivity;
using Octopus.App.Launcher;

namespace Octopus.App.Workspace;

/// <summary>
/// The "+" at the end of one tab group's tab strip. It is placed by <see cref="WorkspaceView"/> and belongs to a group:
/// a click names that group as the one the next new tab opens in and asks for the new-tab menu under it.
/// </summary>
public partial class StripAddButton : UserControl
{
    public StripAddButton() => InitializeComponent();

    /// <summary>Names the button's group as the target of the next new tab; set by the workspace view.</summary>
    public Action? ActivateGroup { get; set; }

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        ActivateGroup?.Invoke();
        LauncherRequest.Raise(Button);
        e.Handled = true;
    }
}
