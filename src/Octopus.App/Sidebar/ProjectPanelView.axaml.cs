using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Octopus.App.Common;
using Octopus.App.Launcher;

namespace Octopus.App.Sidebar;

public partial class ProjectPanelView : UserControl
{
    private ProjectPanelViewModel? _model;

    public ProjectPanelView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_model is not null)
        {
            _model.RenameRequested -= OnRenameRequested;
        }
        _model = DataContext as ProjectPanelViewModel;
        if (_model is not null)
        {
            _model.RenameRequested += OnRenameRequested;
        }
    }

    private void OnRenameRequested(object? sender, EventArgs e) =>
        UiContext.PostToUi(() => NameEdit.BeginEdit(), DispatcherPriority.Input);

    private void OnAddAgentClick(object? sender, RoutedEventArgs e) => LauncherRequest.Raise(AddAgentButton);
}
