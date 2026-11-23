using Octopus.Mobile.Shell;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Account;

/// <summary>Signing in at a service of the person's own: its address, then the code to confirm in the browser.</summary>
public partial class CustomServerPage : Page, IClosablePage
{
    private readonly CustomServerViewModel _viewModel;

    public CustomServerPage(CustomServerViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    public void OnClosed() => _viewModel.Dispose();
}
