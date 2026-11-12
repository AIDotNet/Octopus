using Avalonia.Controls;

namespace Octopus.App.Files.Code;

public partial class CodeViewerView : UserControl
{
    public CodeViewerView() => InitializeComponent();

    public CodeSurface Surface => Body;
}
