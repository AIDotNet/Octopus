using Octopus.Mobile.Shell;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Settings;

/// <summary>Unified mobile Settings Center reached from the "Me" tab's Settings button.</summary>
public partial class SettingsPage : Page, IClosablePage
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    public void OnClosed() => _viewModel.Dispose();
}
