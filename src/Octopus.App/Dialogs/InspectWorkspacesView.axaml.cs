using Avalonia.Controls;
using Octopus.App.Localization;
using TKCode.Arc;

namespace Octopus.App.Dialogs;

public partial class InspectWorkspacesView : UserControl
{
    public InspectWorkspacesView()
    {
        InitializeComponent();
        PopulateFilters();
    }

    public InspectWorkspacesView(InspectWorkspacesViewModel model) : this()
    {
        DataContext = model;
    }

    private void PopulateFilters()
    {
        FilterTabs.Items.Add(new ToggleGroupOption { Value = "all", Label = AppLangResources.Get(AppLangResourceKind.WorkspaceInspectFilterAll), Content = AppLangResources.Get(AppLangResourceKind.WorkspaceInspectFilterAll) });
        FilterTabs.Items.Add(new ToggleGroupOption { Value = "eligible", Label = AppLangResources.Get(AppLangResourceKind.WorkspaceInspectFilterEligible), Content = AppLangResources.Get(AppLangResourceKind.WorkspaceInspectFilterEligible) });
        FilterTabs.Items.Add(new ToggleGroupOption { Value = "protected", Label = AppLangResources.Get(AppLangResourceKind.WorkspaceInspectFilterProtected), Content = AppLangResources.Get(AppLangResourceKind.WorkspaceInspectFilterProtected) });
    }
}
