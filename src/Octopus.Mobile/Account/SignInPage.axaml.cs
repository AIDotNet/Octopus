using Octopus.Mobile.Shell;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Account;

/// <summary>Shown while signed out: the device code to confirm in the browser.</summary>
public partial class SignInPage : Page, IClosablePage
{
    private readonly SignInViewModel _viewModel;

    public SignInPage(SignInViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    public void OnClosed() => _viewModel.Dispose();
}
