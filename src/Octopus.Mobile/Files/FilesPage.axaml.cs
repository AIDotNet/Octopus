using Octopus.Mobile.Shell;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Files;

/// <summary>Browses the files and folders of a project's folder on the host (design/ui/移动端功能扩展方案.html, ⑤).</summary>
public partial class FilesPage : Page, IClosablePage
{
    private readonly FilesViewModel _viewModel;

    public FilesPage(FilesViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        _ = viewModel.LoadAsync();
    }

    public void OnClosed() => _viewModel.Dispose();
}
