using Avalonia.Controls;
using Avalonia.Threading;
using Octopus.App.Localization;
using TKCode.Arc;

namespace Octopus.App.Dialogs;

public partial class CreateWorkspaceView : UserControl
{
    public CreateWorkspaceView()
    {
        InitializeComponent();
        PopulateTabs();
    }

    public CreateWorkspaceView(CreateWorkspaceViewModel model) : this()
    {
        DataContext = model;
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => BranchNameInput.Focus(), DispatcherPriority.Input);
    }

    private void PopulateTabs()
    {
        TaskTabs.Items.Add(new ToggleGroupOption { Value = "none", Label = AppLangResources.Get(AppLangResourceKind.WorkspaceTaskNone), Content = AppLangResources.Get(AppLangResourceKind.WorkspaceTaskNone) });
        TaskTabs.Items.Add(new ToggleGroupOption { Value = "issue", Label = AppLangResources.Get(AppLangResourceKind.WorkspaceTaskIssue), Content = AppLangResources.Get(AppLangResourceKind.WorkspaceTaskIssue) });
        TaskTabs.Items.Add(new ToggleGroupOption { Value = "pr", Label = AppLangResources.Get(AppLangResourceKind.WorkspaceTaskPr), Content = AppLangResources.Get(AppLangResourceKind.WorkspaceTaskPr) });
        TaskTabs.Items.Add(new ToggleGroupOption { Value = "custom", Label = AppLangResources.Get(AppLangResourceKind.WorkspaceTaskCustom), Content = AppLangResources.Get(AppLangResourceKind.WorkspaceTaskCustom) });

        BranchModeTabs.Items.Add(new ToggleGroupOption { Value = "new", Label = AppLangResources.Get(AppLangResourceKind.WorkspaceBranchModeNew), Content = AppLangResources.Get(AppLangResourceKind.WorkspaceBranchModeNew) });
        BranchModeTabs.Items.Add(new ToggleGroupOption { Value = "existing", Label = AppLangResources.Get(AppLangResourceKind.WorkspaceBranchModeExisting), Content = AppLangResources.Get(AppLangResourceKind.WorkspaceBranchModeExisting) });
    }
}
