using Avalonia.Controls;
using TKCode.Arc;
using Octopus.App.Localization;

namespace Octopus.App.Explorer.Files;

/// <summary>The files tab: project name and folder actions, the search box, and the tree or the search results.</summary>
public partial class FilesTabView : UserControl
{
    public FilesTabView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not ExplorerPanelViewModel panel) return;

        var more = new MenuSurface();
        more.Items.Add(new MenuEntry
        {
            Header = AppLangResources.Get(AppLangResourceKind.ExplorerShowHidden),
            ToggleType = MenuEntryToggleType.CheckBox,
            IsChecked = panel.Files.ShowHidden,
            StaysOpenOnClick = false,
            Command = panel.Files.ToggleHiddenCommand,
        });
        more.Items.Add(new MenuEntry
        {
            Header = AppLangResources.Get(AppLangResourceKind.ExplorerRevealProject),
            Command = panel.Files.RevealProjectCommand,
            HasSeparatorBefore = true,
        });
        DropdownMenu.SetMenu(MoreButton, more);
    }
}
