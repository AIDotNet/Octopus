using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Files;

/// <summary>Lists the uncommitted git changes of a project or branch workspace (design/ui/移动端功能扩展方案.html, ⑤).</summary>
public partial class ChangesPage : Page
{
    public ChangesPage(ChangesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _ = viewModel.LoadAsync();
    }
}
