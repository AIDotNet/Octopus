using Octopus.App.Localization;
using Octopus.App.Services;

namespace Octopus.App.Workspace;

/// <summary>The questions asked before something that runs is ended: closing a tab, quitting the app.</summary>
public sealed class CloseConfirmation(ITabContentRegistry registry, IDialogService dialogs)
{
    /// <summary>True when the tab may close: nothing runs in it besides its shell and it has no unsaved edits, or the person agreed.</summary>
    public async Task<bool> ConfirmCloseTabAsync(string itemId)
    {
        var content = registry.Find(itemId);
        if (content is null)
        {
            return true;
        }
        if (await content.HasUnsavedChangesAsync())
        {
            return await dialogs.ConfirmAsync(
                AppLangResources.Get(AppLangResourceKind.CloseUnsavedTitle),
                AppLangResources.Get(AppLangResourceKind.CloseUnsavedBody),
                AppLangResources.Get(AppLangResourceKind.CloseDiscard),
                isDanger: true);
        }
        if (!await content.HasRunningChildAsync())
        {
            return true;
        }
        return await dialogs.ConfirmAsync(
            AppLangResources.Get(AppLangResourceKind.CloseRunningTitle),
            AppLangResources.Get(AppLangResourceKind.CloseRunningBody),
            AppLangResources.Get(AppLangResourceKind.CloseItem),
            isDanger: true);
    }

    /// <summary>True when the app may quit: no tab has a running program or unsaved edits, or the person agreed (asked once for all).</summary>
    public async Task<bool> ConfirmQuitAsync()
    {
        if (await registry.AnyUnsavedChangesAsync())
        {
            return await dialogs.ConfirmAsync(
                AppLangResources.Get(AppLangResourceKind.QuitUnsavedTitle),
                AppLangResources.Get(AppLangResourceKind.QuitUnsavedBody),
                AppLangResources.Get(AppLangResourceKind.CloseDiscard),
                isDanger: true);
        }
        if (!await registry.AnyRunningChildAsync())
        {
            return true;
        }
        return await dialogs.ConfirmAsync(
            AppLangResources.Get(AppLangResourceKind.QuitRunningTitle),
            AppLangResources.Get(AppLangResourceKind.QuitRunningBody),
            AppLangResources.Get(AppLangResourceKind.QuitApp),
            isDanger: true);
    }
}
