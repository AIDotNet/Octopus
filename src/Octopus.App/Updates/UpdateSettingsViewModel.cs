using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;
using Octopus.App.Localization;
using Octopus.Core.Updates;
using Octopus.Platform;
using Octopus.Platform.Updates;
using Octopus.Storage;
using Octopus.Updates;
using TKCode.Arc;
using TKCode.Arc.Localization;

namespace Octopus.App.Updates;

/// <summary>The "Updates" section of the about settings page (design/10-自动更新.md §9).</summary>
public sealed partial class UpdateSettingsViewModel : ObservableObject, IDisposable
{
    private const string FollowChannel = "follow";

    private readonly IUpdateService _updates;
    private readonly ISettingsStore _settings;
    private readonly UpdateCardViewModel _card;
    private readonly IFileOpener _opener;

    public UpdateSettingsViewModel(IUpdateService updates, ISettingsStore settings, UpdateCardViewModel card, IFileOpener opener)
    {
        _updates = updates;
        _settings = settings;
        _card = card;
        _opener = opener;
        Modes = BuildModes();
        Channels = BuildChannels(updates);
        updates.Changed += OnChanged;
        ArcLanguage.Changed += OnLanguageChanged;
        Refresh();
    }

    [ObservableProperty] public partial IReadOnlyList<SelectOption> Modes { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<SelectOption> Channels { get; private set; } = [];

    [ObservableProperty] public partial string VersionText { get; private set; } = "";
    [ObservableProperty] public partial string VersionBadgeText { get; private set; } = "";
    [ObservableProperty] public partial string ChannelBadgeText { get; private set; } = "";
    [ObservableProperty] public partial string StatusText { get; private set; } = "";
    /// <summary>Why this install only offers downloads; empty when it can update itself.</summary>
    [ObservableProperty] public partial string InstallNote { get; private set; } = "";
    [ObservableProperty] public partial bool IsEnabled { get; private set; }
    [ObservableProperty] public partial bool IsChecking { get; private set; }
    [ObservableProperty] public partial bool IsAvailable { get; private set; }
    [ObservableProperty] public partial bool IsDownloading { get; private set; }
    [ObservableProperty] public partial bool IsReady { get; private set; }
    [ObservableProperty] public partial bool HasError { get; private set; }
    [ObservableProperty] public partial bool IsPreviewBuild { get; private set; }
    [ObservableProperty] public partial bool ShowStableBadge { get; private set; }
    [ObservableProperty] public partial bool ShowPreviewBadge { get; private set; }
    [ObservableProperty] public partial bool ShowCheckButton { get; private set; }
    [ObservableProperty] public partial bool IsStatusWarning { get; private set; }
    [ObservableProperty] public partial bool IsStatusInfo { get; private set; }
    [ObservableProperty] public partial bool IsStatusSuccess { get; private set; }

    public bool HasInstallNote => InstallNote.Length > 0;

    partial void OnInstallNoteChanged(string value) => OnPropertyChanged(nameof(HasInstallNote));

    public string Mode
    {
        get => _settings.Current.Updates.Mode;
        set
        {
            if (string.IsNullOrEmpty(value) || value == Mode || !UpdateSettingValues.IsKnownMode(value)) return;
            _settings.Update(s => s.Updates.Mode = value);
            OnPropertyChanged();
        }
    }

    public string Channel
    {
        get => _settings.Current.Updates.Channel ?? FollowChannel;
        set
        {
            if (string.IsNullOrEmpty(value) || value == Channel) return;
            var saved = value == FollowChannel ? null : value;
            if (!UpdateSettingValues.IsKnownChannel(saved)) return;
            _settings.Update(s => s.Updates.Channel = saved);
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private Task Check() => _updates.CheckAsync(manual: true);

    [RelayCommand]
    private void Download() => _card.DownloadCommand.Execute(null);

    [RelayCommand]
    private void Restart() => _card.RestartCommand.Execute(null);

    [RelayCommand]
    private void OpenNotes()
    {
        var s = _updates.Current;
        if (s.Offer is not null)
        {
            _card.OpenNotesCommand.Execute(null);
            return;
        }
        var url = s.CurrentVersion is { } version
            ? UpdateEndpoints.ReleasePage(version)
            : $"{UpdateEndpoints.RepositoryUrl}/releases";
        _opener.OpenUrl(url);
    }

    public void Dispose()
    {
        _updates.Changed -= OnChanged;
        ArcLanguage.Changed -= OnLanguageChanged;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e)
    {
        UiContext.PostToUi(() =>
        {
            Modes = BuildModes();
            Channels = BuildChannels(_updates);
            Refresh();
        });
    }

    private static IReadOnlyList<SelectOption> BuildModes() =>
    [
        new SelectOption(UpdateSettingValues.Auto, AppLangResources.Get(AppLangResourceKind.SettingsUpdateModeAuto)),
        new SelectOption(UpdateSettingValues.Notify, AppLangResources.Get(AppLangResourceKind.SettingsUpdateModeNotify)),
        new SelectOption(UpdateSettingValues.Off, AppLangResources.Get(AppLangResourceKind.SettingsUpdateModeOff)),
    ];

    private static IReadOnlyList<SelectOption> BuildChannels(IUpdateService updates)
    {
        var running = updates.Current.CurrentVersion?.IsPreview == true
            ? AppLangResources.Get(AppLangResourceKind.SettingsUpdateChannelPreview)
            : AppLangResources.Get(AppLangResourceKind.SettingsUpdateChannelStable);
        return
        [
            new SelectOption(FollowChannel, AppLangResources.Format(AppLangResourceKind.SettingsUpdateChannelFollow, running)),
            new SelectOption(UpdateSettingValues.Stable, AppLangResources.Get(AppLangResourceKind.SettingsUpdateChannelStable)),
            new SelectOption(UpdateSettingValues.Preview, AppLangResources.Get(AppLangResourceKind.SettingsUpdateChannelPreview)),
        ];
    }

    private void Refresh()
    {
        var s = _updates.Current;
        IsEnabled = s.Status != UpdateStatus.Disabled;
        IsChecking = s.IsChecking;
        IsAvailable = s.Status == UpdateStatus.Available && s.CanInstall;
        IsDownloading = s.Status == UpdateStatus.Downloading;
        IsReady = s.Status == UpdateStatus.Ready;
        HasError = s.Error is not null && s.Status is UpdateStatus.Failed;
        ShowCheckButton = !IsReady && !IsAvailable;
        IsPreviewBuild = s.CurrentVersion?.IsPreview == true;
        ShowStableBadge = s.CurrentVersion is not null && !IsPreviewBuild;
        ShowPreviewBadge = s.CurrentVersion is not null && IsPreviewBuild;

        IsStatusWarning = HasError || s.Status == UpdateStatus.Available;
        IsStatusInfo = !IsStatusWarning && (IsChecking || IsDownloading || !IsEnabled);
        IsStatusSuccess = !IsStatusWarning && !IsStatusInfo;

        VersionText = s.CurrentVersion is { } version
            ? version.IsPreview ? AppLangResources.Format(AppLangResourceKind.SettingsUpdateVersionPreview, version.ToString()) : version.ToString()
            : "—";
        VersionBadgeText = s.CurrentVersion is { } currentVersion
            ? $"v{currentVersion}"
            : AppLangResources.Get(AppLangResourceKind.SettingsAboutChannelDev);
        ChannelBadgeText = !IsEnabled
            ? AppLangResources.Get(AppLangResourceKind.SettingsAboutChannelDev)
            : IsPreviewBuild
                ? AppLangResources.Get(AppLangResourceKind.SettingsAboutChannelPreview)
                : AppLangResources.Get(AppLangResourceKind.SettingsAboutChannelStable);
        InstallNote = IsEnabled && s.InstallKind != InstallKind.Installed ? UpdateText.Unsupported(s.InstallKind) : "";
        StatusText = StatusOf(s);
    }

    private static string StatusOf(UpdateSnapshot s)
    {
        if (s.Status == UpdateStatus.Disabled) return AppLangResources.Get(AppLangResourceKind.SettingsUpdateDisabled);
        if (s.IsChecking) return AppLangResources.Get(AppLangResourceKind.SettingsUpdateStatusChecking);
        var version = s.Offer?.Version.ToString() ?? "";
        return s.Status switch
        {
            UpdateStatus.Available => AppLangResources.Format(AppLangResourceKind.UpdateAvailableTitle, version),
            UpdateStatus.Downloading => AppLangResources.Format(
                AppLangResourceKind.SettingsUpdateStatusDownloading, version, s.Progress is { } p ? UpdateText.Percent(p) : ""),
            UpdateStatus.Ready => AppLangResources.Format(AppLangResourceKind.SettingsUpdateStatusReady, version),
            UpdateStatus.Failed => UpdateText.Error(s),
            _ when s.LastCheckedAt is { } at => AppLangResources.Format(
                AppLangResourceKind.SettingsUpdateStatusUpToDate, RelativeTime.Format(at, DateTimeOffset.Now)),
            _ => AppLangResources.Get(AppLangResourceKind.SettingsUpdateStatusNever),
        };
    }
}
