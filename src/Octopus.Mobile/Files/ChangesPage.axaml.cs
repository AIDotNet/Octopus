using Octopus.Mobile.Shell;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Files;

/// <summary>Lists the uncommitted git changes of a project or branch workspace (design/ui/移动端功能扩展方案.html, ⑤).</summary>
public partial class ChangesPage : Page, IClosablePage
{
    private readonly ChangesViewModel _viewModel;

    public ChangesPage(ChangesViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        _ = viewModel.LoadAsync();
    }

    public void OnClosed() => _viewModel.Dispose();
}
