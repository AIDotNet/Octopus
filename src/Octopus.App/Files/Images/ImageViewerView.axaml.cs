using Avalonia.Controls;

namespace Octopus.App.Files.Images;

public partial class ImageViewerView : UserControl, IFileViewerView
{
    public ImageViewerView() => InitializeComponent();

    public ImageCanvas Canvas => Picture;

    public void FocusViewer() => Picture.Focus();
}
