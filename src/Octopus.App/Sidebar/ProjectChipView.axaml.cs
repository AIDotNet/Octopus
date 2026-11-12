using Avalonia.Controls;
using Octopus.App.Common;
using Octopus.App.Localization;

namespace Octopus.App.Sidebar;

public partial class ProjectChipView : UserControl
{
    public ProjectChipView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not ProjectChipViewModel chip)
        {
            return;
        }
        MenuFactory.Attach(ChipButton,
        [
            new(AppLangResources.Get(AppLangResourceKind.RenameProject), chip.RenameCommand),
            new(AppLangResources.Get(AppLangResourceKind.RevealInFileManager), chip.RevealCommand),
            new(AppLangResources.Get(AppLangResourceKind.RemoveProject), chip.RemoveCommand, IsDestructive: true, SeparatorBefore: true),
        ]);
    }
}
