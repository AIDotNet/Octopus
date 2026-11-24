using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Files;

/// <summary>Shows a single text file of the host read-only with line numbers (design/ui/移动端功能扩展方案.html, ⑤).</summary>
public partial class FileViewerPage : Page
{
    public FileViewerPage(FileViewerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _ = viewModel.LoadAsync();
    }
}
