using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Common;

/// <summary>One row of a session's menu.</summary>
public sealed record SessionMenuEntry(string Label, ICommand Command, bool IsDestructive = false);

/// <summary>
/// What can be done to a session wherever it shows: the long-press menu of its row, the "⋯" of its terminal page and
/// its details page (design/ui/移动端功能扩展方案.html, ② and M-C). One instance per page; on the host page it acts on
/// whichever row was pressed last. The menu, the rename sheet and the close confirmation are drawn by
/// <see cref="SessionActionOverlays"/>.
/// </summary>
public sealed partial class SessionActionsViewModel : ObservableObject
{
    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;
    private readonly Action<RemoteSession>? _closed;

    /// <param name="closed">Runs after the session was closed on the host: the page showing only it leaves.</param>
    public SessionActionsViewModel(IRemoteWorkspace workspace, INavigator navigator, RemoteSession? session = null, Action<RemoteSession>? closed = null)
    {
        _workspace = workspace;
        _navigator = navigator;
        _closed = closed;
        Session = session;
        RenameText = "";
    }

    /// <summary>The session acted on.</summary>
    [ObservableProperty] public partial RemoteSession? Session { get; set; }

    [ObservableProperty] public partial bool IsRenameOpen { get; set; }
    [ObservableProperty] public partial string RenameText { get; set; }
    [ObservableProperty] public partial bool IsCloseOpen { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; private set; }

    public string CloseTitle => string.Format(MobileLangResources.Get(MobileLangResourceKind.SessionCloseTitle), Session?.Title);
    /// <summary>A GUI session has no terminal: no restart, and opening it shows its details.</summary>
    public bool CanRestart => Session is { IsGui: false };

    /// <summary>The menu to show: its title, a line under it, and the rows. Raised by <see cref="ShowMenu"/>.</summary>
    public event Action<string, string?, IReadOnlyList<SessionMenuEntry>>? MenuRequested;

    /// <summary>A short message for the person (done, or why not).</summary>
    public event Action<string>? Notice;

    /// <summary>
    /// Opens the menu of <paramref name="session"/>: <paramref name="leading"/> first (what only this page offers),
    /// then open (when <paramref name="includeOpen"/>), rename, restart, another like it, details and close.
    /// </summary>
    public void ShowMenu(RemoteSession session, IEnumerable<SessionMenuEntry>? leading = null, bool includeOpen = false, bool includeDetails = true)
    {
        Session = session;
        var entries = new List<SessionMenuEntry>(leading ?? []);
        if (includeOpen) entries.Add(new(Text(MobileLangResourceKind.SessionOpen), OpenCommand));
        entries.Add(new(Text(MobileLangResourceKind.SessionRename), BeginRenameCommand));
        if (CanRestart) entries.Add(new(Text(MobileLangResourceKind.SessionRestart), RestartCommand));
        entries.Add(new(Text(MobileLangResourceKind.SessionDuplicate), DuplicateCommand));
        if (includeDetails) entries.Add(new(Text(MobileLangResourceKind.SessionDetails), ShowDetailsCommand));
        entries.Add(new(Text(MobileLangResourceKind.SessionClose), BeginCloseCommand, IsDestructive: true));
        MenuRequested?.Invoke(session.Title, Summary(session), entries);
    }

    [RelayCommand]
    private void Open()
    {
        if (Session is { } session) _navigator.ShowSession(session);
    }

    [RelayCommand]
    private void ShowDetails()
    {
        if (Session is { } session) _navigator.ShowSessionDetails(session);
    }

    [RelayCommand]
    private void BeginRename()
    {
        if (Session is null) return;
        RenameText = Session.Title;
        IsRenameOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmRenameAsync()
    {
        if (Session is not { } session || IsBusy) return;
        await RunAsync(() => _workspace.RenameSessionAsync(session, RenameText));
        IsRenameOpen = false;
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        if (Session is not { IsGui: false } session) return;
        if (await RunAsync(() => _workspace.RestartSessionAsync(session))) Notice?.Invoke(Text(MobileLangResourceKind.SessionRestarted));
    }

    /// <summary>Starts a session like this one (same agent, model, effort and workspace, no prompt) and opens it.</summary>
    [RelayCommand]
    private async Task DuplicateAsync()
    {
        if (Session is not { } session) return;
        RemoteSession? created = null;
        var request = new NewSessionRequest(session.AgentId, session.Model, session.Effort, null, session.WorkspaceId);
        if (await RunAsync(async () => created = await _workspace.CreateSessionAsync(session.Project, request)) && created is not null)
        {
            _navigator.ShowSession(created);
        }
    }

    [RelayCommand]
    private void BeginClose()
    {
        if (Session is null) return;
        OnPropertyChanged(nameof(CloseTitle));
        IsCloseOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmCloseAsync()
    {
        IsCloseOpen = false;
        if (Session is not { } session) return;
        if (await RunAsync(() => _workspace.CloseSessionAsync(session))) _closed?.Invoke(session);
    }

    partial void OnSessionChanged(RemoteSession? value)
    {
        OnPropertyChanged(nameof(CloseTitle));
        OnPropertyChanged(nameof(CanRestart));
    }

    /// <summary>Runs a call on the host; what goes wrong becomes a notice. False when it failed.</summary>
    private async Task<bool> RunAsync(Func<Task> call)
    {
        IsBusy = true;
        try
        {
            await call();
            return true;
        }
        catch (IOException e)
        {
            Notice?.Invoke(e.Message);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // What it runs and what it is doing, under the menu's title.
    private static string? Summary(RemoteSession session)
    {
        var parts = new[]
        {
            session.Detail,
            session.IsGui ? Text(MobileLangResourceKind.SurfaceGui) : null,
            session.Preview ?? (session.Activity == SessionActivity.Idle ? null : SessionRowViewModel.StatusOf(session.Activity)),
        };
        return string.Join(" · ", parts.Where(part => !string.IsNullOrEmpty(part))) is { Length: > 0 } text ? text : null;
    }

    private static string Text(MobileLangResourceKind kind) => MobileLangResources.Get(kind);
}
