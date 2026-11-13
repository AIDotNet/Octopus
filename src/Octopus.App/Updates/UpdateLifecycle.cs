using Octopus.App.Composition;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Platform;
using Octopus.Updates;
using TKCode.Arc;

namespace Octopus.App.Updates;

/// <summary>Where the app's start and end meet the updater (design/10-自动更新.md §7).</summary>
internal static class UpdateLifecycle
{
    /// <summary>Starts the updater; says so once when the last quit installed a new version.</summary>
    public static void Start()
    {
        try
        {
            var updates = AppHost.Get<IUpdateService>();
            updates.Start();
            AppHost.Get<UpdateNotesPresenter>().Start();
            if (updates.JustUpdatedTo is { } version && AppHost.Get<IWindowAccessor>().Window is { } window)
            {
                var notes = UpdateEndpoints.ReleasePage(version);
                Toast.Show(
                    window,
                    AppLangResources.Format(AppLangResourceKind.UpdateUpdatedTo, version.ToString()),
                    ToastKind.Success,
                    null!,
                    new ToastAction(AppLangResources.Get(AppLangResourceKind.UpdateNotes), () => AppHost.Get<IFileOpener>().OpenUrl(notes)),
                    TimeSpan.FromSeconds(8),
                    null!);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // An updater that cannot start leaves the app as it is.
        }
    }

    /// <summary>Tells about the notes of a version that was found while the window could not show them.</summary>
    public static void PresentNotes()
    {
        try { AppHost.Get<UpdateNotesPresenter>().PresentIfDue(); }
        catch (InvalidOperationException) { }
    }

    /// <summary>Called as the app ends: a ready update is handed to its installer, which waits for the process to end.</summary>
    public static void InstallOnExit()
    {
        try
        {
            AppHost.Get<IUpdateService>().InstallOnExit();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ObjectDisposedException)
        {
            // Quitting must never fail because of an update.
        }
    }
}
