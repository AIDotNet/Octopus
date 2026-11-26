using Octopus.Mobile.Shell;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Files;

/// <summary>Shows a single text, Markdown, or image file of the host read-only (design/ui/移动端功能扩展方案.html, ⑤).</summary>
public partial class FileViewerPage : Page, IClosablePage
{
    private readonly FileViewerViewModel _viewModel;

    public FileViewerPage(FileViewerViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        _ = viewModel.LoadAsync();
    }

    public void OnClosed() => _viewModel.Dispose();
}
