using Avalonia.Controls;
using Octopus.App.Localization;
using Octopus.Terminal.Avalonia;
using TKCode.Arc;

namespace Octopus.App.Terminals;

/// <summary>The dialog that asks before several lines are pasted into a terminal.</summary>
public static class PasteConfirmation
{
    /// <summary>True when the person chose to paste.</summary>
    public static async Task<bool> ShowAsync(Control owner, PastePreview preview)
    {
        var result = await Dialog.ShowAsync(
            owner,
            AppLangResources.Format(AppLangResourceKind.TerminalPasteTitle, preview.LineCount),
            new PastePreviewView(preview),
            AppLangResources.Get(AppLangResourceKind.TerminalPasteDescription),
            new DialogAction(AppLangResources.Get(AppLangResourceKind.Cancel), false),
            new DialogAction(AppLangResources.Get(AppLangResourceKind.TerminalPasteConfirm), true, ButtonVariant.Primary));
        return result is true;
    }
}
