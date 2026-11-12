using Avalonia.Controls;
using Avalonia.Interactivity;
using Octopus.App.Launcher;

namespace Octopus.App.Workspace;

public partial class EmptyProjectView : UserControl
{
    public EmptyProjectView() => InitializeComponent();

    private void OnAgentClick(object? sender, RoutedEventArgs e) => LauncherRequest.Raise(AgentButton);
}
