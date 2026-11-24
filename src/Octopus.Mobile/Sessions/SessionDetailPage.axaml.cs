using Octopus.Mobile.Shell;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Sessions;

/// <summary>One session's details and actions (design/ui/移动端功能扩展方案.html, M-C ②).</summary>
public partial class SessionDetailPage : Page, IClosablePage
{
    private readonly SessionDetailViewModel _viewModel;

    public SessionDetailPage(SessionDetailViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
    }

    public void OnClosed() => _viewModel.Dispose();
}
