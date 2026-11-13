using Avalonia.Threading;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Platform;
using Octopus.Updates;
using TKCode.Arc;

namespace Octopus.App.Updates;

/// <summary>
/// Shows what is new in an update, rendered as Markdown. The dialog opens by itself once for each version the updater
/// finds (or has ready from the last run), and again when the card's "Release notes" is clicked. Its buttons follow
/// the card: download, restart, skip or the download page, whichever the card offers.
/// </summary>
internal sealed class UpdateNotesPresenter(IUpdateService updates, UpdateCardViewModel card, IWindowAccessor windows, IFileOpener opener)
{
    private const string Later = "later", Skip = "skip", Download = "download", Restart = "restart", Page = "page";

    /// <summary>The versions whose notes were already shown in this run.</summary>
    private readonly HashSet<string> _shown = [];
    private bool _open;
    private bool _started;

    public void Start()
    {
        if (_started) return;
        _started = true;
        updates.Changed += (_, _) => Dispatcher.UIThread.Post(PresentIfDue);
        card.NotesRequested += (_, _) => _ = ShowAsync();
        PresentIfDue();
    }

    /// <summary>Opens the dialog for a version that was not shown yet, when the window can show it.</summary>
    public void PresentIfDue()
    {
        var snapshot = updates.Current;
        if (_open || windows.Window is not { IsVisible: true } || snapshot.Offer is not { Notes: not null } offer) return;
        if (UpdateCardViewModel.StateOf(snapshot) is UpdateCardState.Hidden or UpdateCardState.Failed) return;
        if (!_shown.Add(offer.Version.ToString())) return;
        _ = ShowAsync();
    }

    /// <summary>The dialog for the release on offer; the release page when it has no notes.</summary>
    public async Task ShowAsync()
    {
        if (_open || windows.Window is not { } window || updates.Current.Offer is not { } offer) return;
        if (offer.Notes is not { } notes)
        {
            if (offer.NotesUrl is { } url) opener.OpenUrl(url);
            return;
        }
        _open = true;
        try
        {
            var later = new DialogAction(AppLangResources.Get(AppLangResourceKind.UpdateLater), Later, ButtonVariant.Secondary);
            DialogAction[] actions = UpdateCardViewModel.StateOf(updates.Current) switch
            {
                UpdateCardState.Available =>
                [
                    new DialogAction(AppLangResources.Get(AppLangResourceKind.UpdateSkip), Skip, ButtonVariant.Secondary),
                    later,
                    new DialogAction(AppLangResources.Get(AppLangResourceKind.UpdateDownload), Download, ButtonVariant.Primary),
                ],
                UpdateCardState.Unsupported =>
                [
                    later,
                    new DialogAction(AppLangResources.Get(AppLangResourceKind.UpdateOpenPage), Page, ButtonVariant.Primary),
                ],
                UpdateCardState.Ready =>
                [
                    later,
                    new DialogAction(AppLangResources.Get(AppLangResourceKind.UpdateRestart), Restart, ButtonVariant.Primary),
                ],
                // Downloading goes on behind the dialog; there is nothing to decide.
                _ => [new DialogAction(AppLangResources.Get(AppLangResourceKind.UpdateClose), Later, ButtonVariant.Primary)],
            };
            var result = await Dialog.ShowAsync(
                window,
                AppLangResources.Format(AppLangResourceKind.UpdateNotesTitle, offer.Version.ToString()),
                new UpdateNotesView(notes),
                UpdateText.Release(offer),
                actions);
            switch (result as string)
            {
                case Skip: card.SkipCommand.Execute(null); break;
                case Download: card.DownloadCommand.Execute(null); break;
                case Restart: card.RestartCommand.Execute(null); break;
                case Page: card.OpenPageCommand.Execute(null); break;
            }
        }
        finally
        {
            _open = false;
        }
    }
}
