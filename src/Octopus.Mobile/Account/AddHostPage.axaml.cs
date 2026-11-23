using Octopus.Mobile.Shell;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Account;

/// <summary>Adding a computer by its address or a pairing link (~/code/Octopus/design/mobile/01-登录与连接方式.md §4).</summary>
public partial class AddHostPage : Page, IClosablePage
{
    private readonly AddHostViewModel _viewModel;

    public AddHostPage(AddHostViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    public void OnClosed() => _viewModel.Dispose();
}
