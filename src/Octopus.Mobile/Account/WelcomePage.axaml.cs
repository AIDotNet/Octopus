using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Account;

/// <summary>
/// The first page of a phone that has not chosen how to connect: "Sign in", and the other ways
/// (~/code/Octopus/design/mobile/01-登录与连接方式.md §3). On top of the host list it has a way back.
/// </summary>
public partial class WelcomePage : Page, IClosablePage
{
    private readonly WelcomeViewModel _viewModel;

    public WelcomePage(WelcomeViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        if (viewModel.IsOnTop)
        {
            Header = new Navbar
            {
                ShowBack = true,
                BackText = MobileLangResources.Get(MobileLangResourceKind.Hosts),
                BackCommand = viewModel.BackCommand,
            };
        }
    }

    public void OnClosed() => _viewModel.Dispose();
}
