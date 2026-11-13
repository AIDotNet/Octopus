using Avalonia.Controls;
using Avalonia.Input;

namespace Octopus.App.Explorer.Sessions;

/// <summary>The sessions tab: the agents, then one agent's sessions by account (design/ui/会话管理方案.html, layout A).</summary>
public partial class SessionsTabView : UserControl
{
    public SessionsTabView()
    {
        InitializeComponent();
    }

    /// <summary>A double click on a session continues it, like its play button.</summary>
    private void OnSessionDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: SessionRowViewModel row } && row.ContinueCommand.CanExecute(null))
        {
            row.ContinueCommand.Execute(null);
        }
    }
}
