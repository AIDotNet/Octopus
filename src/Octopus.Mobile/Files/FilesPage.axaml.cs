using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Files;

/// <summary>Browses the files and folders of a project's folder on the host (design/ui/移动端功能扩展方案.html, ⑤).</summary>
public partial class FilesPage : Page
{
    public FilesPage(FilesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _ = viewModel.LoadAsync();
    }
}
