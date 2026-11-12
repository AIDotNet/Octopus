using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Localization;
using Octopus.App.Services;
using Octopus.Platform;
using Octopus.Updates;

namespace Octopus.App.Updates;

/// <summary>What the update card shows (design/10-自动更新.md §10.2).</summary>
public enum UpdateCardState
{
    Hidden,
    /// <summary>Found, not downloading: notify mode, or a cancelled download.</summary>
    Available,
    /// <summary>Found, but this install cannot update itself: only the download page.</summary>
    Unsupported,
    Downloading,
    Ready,
    Failed,
}

/// <summary>
/// The card at the bottom right: tells about a new version, shows the download, offers the restart. Follows the
/// updater's snapshots; closing it hides the current state only, and the next state (downloaded, failed) shows again.
/// </summary>
public sealed partial class UpdateCardViewModel : ObservableObject
{
    private readonly IUpdateService _updates;
    private readonly IFileOpener _opener;
    private readonly IWindowAccessor _window;
    /// <summary>The state and version the person closed the card on.</summary>
    private (UpdateCardState State, string? Version)? _dismissed;

    public UpdateCardViewModel(IUpdateService updates, IFileOpener opener, IWindowAccessor window)
    {
        _updates = updates;
        _opener = opener;
        _window = window;
        updates.Changed += (_, _) =>
        {
            if (Dispatcher.UIThread.CheckAccess()) Refresh();
            else Dispatcher.UIThread.Post(Refresh);
        };
        Refresh();
    }

    [ObservableProperty] public partial UpdateCardState State { get; private set; }
    [ObservableProperty] public partial bool IsOpen { get; private set; }
    [ObservableProperty] public partial string Title { get; private set; } = "";
    [ObservableProperty] public partial string Detail { get; private set; } = "";
    [ObservableProperty] public partial bool HasNotes { get; private set; }
    [ObservableProperty] public partial double ProgressValue { get; private set; }
    /// <summary>"38.2 / 61.0 MB", over the bar.</summary>
    [ObservableProperty] public partial string ProgressText { get; private set; } = "";
    /// <summary>"4.2 MB/s · about 6 s left", under the bar.</summary>
    [ObservableProperty] public partial string SpeedText { get; private set; } = "";

    public bool ShowArrowIcon => State is UpdateCardState.Available or UpdateCardState.Unsupported or UpdateCardState.Downloading;
    public bool IsDownloading => State == UpdateCardState.Downloading;
    public bool IsReady => State == UpdateCardState.Ready;
    public bool IsFailed => State == UpdateCardState.Failed;
    public bool ShowSkip => State == UpdateCardState.Available;
    public bool ShowDownload => State == UpdateCardState.Available;
    public bool ShowLater => State is UpdateCardState.Available or UpdateCardState.Unsupported or UpdateCardState.Ready;
    public bool ShowCancel => State == UpdateCardState.Downloading;
    public bool ShowRestart => State == UpdateCardState.Ready;
    public bool ShowOpenPage => State == UpdateCardState.Unsupported || (State == UpdateCardState.Failed && HasNotes);
    public bool ShowRetry => State == UpdateCardState.Failed && _updates.Current is { CanInstall: true, Error: not UpdateErrorKind.Install };

    partial void OnStateChanged(UpdateCardState value)
    {
        OnPropertyChanged(nameof(ShowArrowIcon));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(ShowSkip));
        OnPropertyChanged(nameof(ShowDownload));
        OnPropertyChanged(nameof(ShowLater));
        OnPropertyChanged(nameof(ShowCancel));
        OnPropertyChanged(nameof(ShowRestart));
        OnPropertyChanged(nameof(ShowOpenPage));
        OnPropertyChanged(nameof(ShowRetry));
    }

    partial void OnHasNotesChanged(bool value) => OnPropertyChanged(nameof(ShowOpenPage));

    /// <summary>The card's state for a snapshot of the updater.</summary>
    public static UpdateCardState StateOf(UpdateSnapshot s) => s.Status switch
    {
        UpdateStatus.Available when s.Offer is not null => s.CanInstall ? UpdateCardState.Available : UpdateCardState.Unsupported,
        UpdateStatus.Downloading => UpdateCardState.Downloading,
        UpdateStatus.Ready => UpdateCardState.Ready,
        UpdateStatus.Failed when !s.IsQuietError => UpdateCardState.Failed,
        _ => UpdateCardState.Hidden,
    };

    private void Refresh()
    {
        var s = _updates.Current;
        var state = StateOf(s);
        var version = s.Offer?.Version.ToString();
        State = state;
        HasNotes = WebUrl.IsHttp(s.Offer?.NotesUrl);
        switch (state)
        {
            case UpdateCardState.Available:
                Title = AppLangResources.Format(AppLangResourceKind.UpdateAvailableTitle, version!);
                Detail = UpdateText.Release(s.Offer!);
                break;
            case UpdateCardState.Unsupported:
                Title = AppLangResources.Format(AppLangResourceKind.UpdateAvailableTitle, version!);
                Detail = UpdateText.Release(s.Offer!) + "\n" + UpdateText.Unsupported(s.InstallKind);
                break;
            case UpdateCardState.Downloading:
                Title = AppLangResources.Format(AppLangResourceKind.UpdateDownloadingTitle, version!);
                Detail = UpdateText.Release(s.Offer!);
                if (s.Progress is { } progress)
                {
                    ProgressValue = progress.Fraction * 100;
                    ProgressText = UpdateText.Size(progress);
                    SpeedText = UpdateText.Speed(progress);
                }
                break;
            case UpdateCardState.Ready:
                Title = AppLangResources.Format(AppLangResourceKind.UpdateReadyTitle, version!);
                Detail = AppLangResources.Get(AppLangResourceKind.UpdateReadyDetail);
                break;
            case UpdateCardState.Failed:
                Title = AppLangResources.Get(AppLangResourceKind.UpdateFailedTitle);
                Detail = UpdateText.Error(s);
                break;
        }
        if (state != UpdateCardState.Downloading)
        {
            ProgressValue = 0;
            ProgressText = "";
            SpeedText = "";
        }
        OnPropertyChanged(nameof(ShowRetry));
        IsOpen = state != UpdateCardState.Hidden && _dismissed != (state, version);
    }

    [RelayCommand]
    private void Close()
    {
        _dismissed = (State, _updates.Current.Offer?.Version.ToString());
        IsOpen = false;
    }

    [RelayCommand]
    private void Later() => Close();

    [RelayCommand]
    private void Skip() => _updates.SkipOffered();

    [RelayCommand]
    private void Download() => _ = _updates.DownloadAsync();

    [RelayCommand]
    private void Retry() => _ = _updates.DownloadAsync();

    [RelayCommand]
    private void Cancel() => _updates.CancelDownload();

    [RelayCommand]
    private void OpenNotes()
    {
        if (_updates.Current.Offer?.NotesUrl is { } url) _opener.OpenUrl(url);
    }

    [RelayCommand]
    private void OpenPage() => OpenNotes();

    /// <summary>Quits through the normal path (it asks about running programs); the helper then starts the new version.</summary>
    [RelayCommand]
    private void Restart()
    {
        _updates.RelaunchAfterInstall = true;
        if (_window.Window is Window window) window.Close();
    }
}
