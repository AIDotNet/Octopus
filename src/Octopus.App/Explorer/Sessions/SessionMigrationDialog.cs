using Octopus.App.Localization;
using Octopus.App.Services;
using TKCode.Arc;

namespace Octopus.App.Explorer.Sessions;

/// <summary>Asks where and how a session should move.</summary>
public interface ISessionMigrationDialog
{
    /// <summary>The person's choice; null when they cancelled or picked nothing that can take the session.</summary>
    Task<SessionMigrationChoice?> AskAsync(SessionMigrationFormViewModel form);
}

public sealed class SessionMigrationDialog(IWindowAccessor windows) : ISessionMigrationDialog
{
    public async Task<SessionMigrationChoice?> AskAsync(SessionMigrationFormViewModel form)
    {
        if (windows.Window is null) return null;
        var result = await Dialog.ShowAsync(
            windows.Window,
            AppLangResources.Get(AppLangResourceKind.MigrateTitle),
            new SessionMigrationView(form),
            AppLangResources.Get(AppLangResourceKind.MigrateDescription),
            new DialogAction(AppLangResources.Get(AppLangResourceKind.Cancel), false),
            new DialogAction(AppLangResources.Get(AppLangResourceKind.MigrateConfirm), true, ButtonVariant.Primary));
        return result is true ? form.Choice : null;
    }
}
