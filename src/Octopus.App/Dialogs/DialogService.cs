using Octopus.App.Localization;
using Octopus.App.Services;
using TKCode.Arc;

namespace Octopus.App.Dialogs;

/// <summary>Confirmations as Arc dialogs over the main window.</summary>
public sealed class DialogService(IWindowAccessor windows, IPickerService pickers) : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string description, string confirmText, bool isDanger = false)
    {
        if (windows.Window is null)
        {
            return Task.FromResult(false);
        }
        return Dialog.ConfirmAsync(
            windows.Window, title, description, confirmText,
            AppLangResources.Get(AppLangResourceKind.Cancel), isDanger);
    }

    public async Task<NewProjectChoice?> AskNewProjectAsync()
    {
        if (windows.Window is null)
        {
            return null;
        }
        NewProjectChoice? typed = null;
        string? error = null;
        while (true)
        {
            var form = new NewProjectView(pickers, typed);
            form.ShowError(error);
            var result = await Dialog.ShowAsync(
                windows.Window,
                AppLangResources.Get(AppLangResourceKind.NewProject),
                form,
                AppLangResources.Get(AppLangResourceKind.NewProjectDescription),
                new DialogAction(AppLangResources.Get(AppLangResourceKind.Cancel), false),
                new DialogAction(AppLangResources.Get(AppLangResourceKind.NewProjectAdd), true, ButtonVariant.Primary));
            if (result is not true)
            {
                return null;
            }
            typed = new NewProjectChoice(form.FolderPath, form.ProjectName);
            error = NewProjectView.Validate(typed.Path);
            if (error is null)
            {
                return typed with { Path = Path.GetFullPath(NewProjectView.ExpandHome(typed.Path)) };
            }
            // Not a usable folder: the dialog comes back with what was typed and says why.
        }
    }
}
