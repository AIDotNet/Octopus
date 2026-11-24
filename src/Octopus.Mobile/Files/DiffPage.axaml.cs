using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Files;

/// <summary>Shows the unified diff of one uncommitted change (design/ui/移动端功能扩展方案.html, ⑤).</summary>
public partial class DiffPage : Page
{
    public DiffPage(DiffViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _ = viewModel.LoadAsync();
    }
}
