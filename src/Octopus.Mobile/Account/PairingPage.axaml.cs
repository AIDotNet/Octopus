using Octopus.Mobile.Shell;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Account;

/// <summary>Pairing with a host: the number to compare with the one on the computer.</summary>
public partial class PairingPage : Page, IClosablePage
{
    private readonly PairingViewModel _viewModel;

    public PairingPage(PairingViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    public void OnClosed() => _viewModel.Dispose();
}
