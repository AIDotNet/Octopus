using Avalonia.Controls;
using Octopus.App.Localization;
using Octopus.Terminal.Avalonia;

namespace Octopus.App.Terminals;

/// <summary>The first lines of a paste, and how many more there are.</summary>
public partial class PastePreviewView : UserControl
{
    public PastePreviewView() : this(new PastePreview(0, [], 0))
    {
    }

    public PastePreviewView(PastePreview preview)
    {
        InitializeComponent();
        Lines.Text = string.Join('\n', preview.Lines);
        More.IsVisible = preview.MoreLines > 0;
        More.Text = preview.MoreLines > 0
            ? AppLangResources.Format(AppLangResourceKind.TerminalPasteMore, preview.MoreLines)
            : "";
    }
}
