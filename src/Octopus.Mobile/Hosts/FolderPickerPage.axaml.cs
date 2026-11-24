using TKCode.Arc.Mobile;

namespace Octopus.Mobile.Hosts;

/// <summary>Picks a folder of a host for a new project (design/ui/移动端功能扩展方案.html, ③ "目录浏览").</summary>
public partial class FolderPickerPage : Page
{
    public FolderPickerPage(FolderPickerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _ = viewModel.LoadAsync();
    }
}
