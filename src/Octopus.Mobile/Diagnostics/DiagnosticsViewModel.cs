using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Common;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;
using TKCode.Arc;

namespace Octopus.Mobile.Diagnostics;

/// <summary>
/// View model for <see cref="DiagnosticsPage"/>: manages opt-in consent (disabled by default), previews redacted
/// crash/runtime logs, exports sanitized <c>.zip</c> bundles via the platform share sheet, copies text summaries via
/// <see cref="CopyButton"/>, or opens a pre-filled GitHub Issue.
/// </summary>
public sealed partial class DiagnosticsViewModel : ObservableObject, IDisposable
{
    private readonly IRemoteWorkspace _workspace;
    private readonly IAccountSession _account;
    private readonly INavigator _navigator;
    private readonly Action<Uri> _openBrowser;
    private bool _syncingConsent;

    public DiagnosticsViewModel(
        IRemoteWorkspace workspace,
        IAccountSession account,
        INavigator navigator,
        Action<Uri>? openBrowser = null)
    {
        _workspace = workspace;
        _account = account;
        _navigator = navigator;
        _openBrowser = openBrowser ?? (_ => { });

        MobileDiagnostics.PendingNoticeChanged += OnDiagnosticsChanged;
        MobileDiagnostics.CollectionEnabledChanged += OnCollectionEnabledChanged;
        Reload();
    }

    /// <summary>Raised when a transient toast notification should be displayed on the page.</summary>
    public event Action<string>? ToastRequested;

    public ObservableCollection<DiagnosticRecordRowViewModel> Records { get; } = [];

    [ObservableProperty] public partial bool IsCollectionEnabled { get; set; }
    [ObservableProperty] public partial bool IsConsentDialogOpen { get; set; }
    [ObservableProperty] public partial bool IsClearDialogOpen { get; set; }
    [ObservableProperty] public partial bool HasRecords { get; private set; }
    [ObservableProperty] public partial bool IsEmpty { get; private set; }
    [ObservableProperty] public partial string EnvironmentSummary { get; private set; } = "";
    [ObservableProperty] public partial string StorageSummary { get; private set; } = "";
    [ObservableProperty] public partial string UserNote { get; set; } = "";
    [ObservableProperty] public partial bool MaskIdentifiers { get; set; } = true;
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial bool IsPreviewOpen { get; set; }
    [ObservableProperty] public partial string PreviewTitle { get; private set; } = "";
    [ObservableProperty] public partial string PreviewText { get; private set; } = "";
    [ObservableProperty] public partial string CopyableSummary { get; private set; } = "";
    [ObservableProperty] public partial string? LastExportPath { get; private set; }
    [ObservableProperty] public partial DiagnosticRecord? SelectedRecord { get; private set; }
    [ObservableProperty] public partial bool HasStoredLogs { get; private set; }

    public bool IsCollectionDisabled => !IsCollectionEnabled;
    public bool ShowEmptyRecords => IsCollectionEnabled && IsEmpty;
    public bool ShowRecordsList => IsCollectionEnabled && HasRecords;
    public bool CanClearLogs => HasStoredLogs || HasRecords;
    public BadgeTone ConsentBadgeTone => IsCollectionEnabled ? BadgeTone.Success : BadgeTone.Neutral;
    public string ConsentBadgeText => MobileLangResources.Get(
        IsCollectionEnabled ? MobileLangResourceKind.DiagnosticsStatusOn : MobileLangResourceKind.DiagnosticsStatusOff);

    partial void OnIsCollectionEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCollectionDisabled));
        OnPropertyChanged(nameof(ShowEmptyRecords));
        OnPropertyChanged(nameof(ShowRecordsList));
        OnPropertyChanged(nameof(ConsentBadgeTone));
        OnPropertyChanged(nameof(ConsentBadgeText));

        if (_syncingConsent) return;

        if (value && !MobileDiagnostics.IsCollectionEnabled)
        {
            // Require explicit confirmation in the consent dialog before turning data collection on.
            _syncingConsent = true;
            IsCollectionEnabled = false;
            _syncingConsent = false;
            IsConsentDialogOpen = true;
            return;
        }

        if (!value && MobileDiagnostics.IsCollectionEnabled)
        {
            MobilePreferences.Current.DiagnosticsCollectionEnabled = false;
            Reload();
            ToastRequested?.Invoke(MobileLangResources.Get(MobileLangResourceKind.DiagnosticsDisabledToast));
        }
    }

    partial void OnMaskIdentifiersChanged(bool value) => UpdatePreviewAndSummary();
    partial void OnUserNoteChanged(string value) => UpdatePreviewAndSummary();

    [RelayCommand]
    private void Back() => _navigator.Back();

    [RelayCommand]
    private void RequestEnableCollection()
    {
        if (MobileDiagnostics.IsCollectionEnabled) return;
        IsConsentDialogOpen = true;
    }

    [RelayCommand]
    private void ConfirmEnableCollection()
    {
        IsConsentDialogOpen = false;
        MobilePreferences.Current.DiagnosticsCollectionEnabled = true;
        Reload();
        ToastRequested?.Invoke(MobileLangResources.Get(MobileLangResourceKind.DiagnosticsEnabledToast));
    }

    [RelayCommand]
    private void Reload()
    {
        _syncingConsent = true;
        IsCollectionEnabled = MobileDiagnostics.IsCollectionEnabled;
        _syncingConsent = false;

        var env = MobileDiagnostics.Platform.GetEnvironment();
        EnvironmentSummary = $"{env.Platform} {env.OsVersion} · {env.DeviceModel} · v{env.AppVersion}";

        var records = MobileDiagnostics.ListRecords();
        Records.Clear();
        foreach (var record in records)
        {
            Records.Add(new DiagnosticRecordRowViewModel(record, SelectRecord));
        }

        HasRecords = Records.Count > 0;
        IsEmpty = Records.Count == 0;
        SelectedRecord = records.FirstOrDefault();
        SyncRowSelection();

        var totalBytes = MobileDiagnostics.GetTotalLogBytes();
        HasStoredLogs = totalBytes > 0;
        StorageSummary = MobileLangResources.Format(
            MobileLangResourceKind.DiagnosticsStorageSummary,
            records.Count,
            FormatBytes(totalBytes));

        OnPropertyChanged(nameof(IsCollectionDisabled));
        OnPropertyChanged(nameof(ShowEmptyRecords));
        OnPropertyChanged(nameof(ShowRecordsList));
        OnPropertyChanged(nameof(CanClearLogs));
        OnPropertyChanged(nameof(ConsentBadgeTone));
        OnPropertyChanged(nameof(ConsentBadgeText));

        UpdatePreviewAndSummary();
    }

    [RelayCommand]
    private async Task ShareBundleAsync()
    {
        if (IsBusy || !IsCollectionEnabled) return;
        IsBusy = true;
        try
        {
            var note = UserNote;
            var mask = MaskIdentifiers;
            var bundle = await Task.Run(() => DiagnosticBundleBuilder.BuildZipBundle(_workspace, _account, note, mask))
                .ConfigureAwait(true);
            LastExportPath = bundle.ZipPath;
            MobileDiagnostics.DismissPendingNotice();

            var title = MobileLangResources.Get(MobileLangResourceKind.DiagnosticsShareTitle);
            if (MobileDiagnostics.Platform.CanShareFiles)
            {
                var shared = await MobileDiagnostics.Platform.ShareFileAsync(bundle.ZipPath, title, "application/zip")
                    .ConfigureAwait(true);
                ToastRequested?.Invoke(shared
                    ? MobileLangResources.Format(MobileLangResourceKind.DiagnosticsExportShared, bundle.ReportId, FormatBytes(bundle.SizeBytes))
                    : MobileLangResources.Get(MobileLangResourceKind.DiagnosticsExportShareFailed));
            }
            else
            {
                ToastRequested?.Invoke(MobileLangResources.Format(MobileLangResourceKind.DiagnosticsExportCreated, bundle.ReportId, FormatBytes(bundle.SizeBytes)));
            }
        }
        catch (Exception ex)
        {
            MobileDiagnostics.Error("DIAG", "Building or sharing diagnostic bundle failed", ex);
            ToastRequested?.Invoke(MobileLangResources.Get(MobileLangResourceKind.DiagnosticsExportShareFailed));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenGitHubIssue()
    {
        if (!IsCollectionEnabled) return;
        try
        {
            var uri = DiagnosticBundleBuilder.BuildGitHubIssueUri(_workspace, _account, UserNote, MaskIdentifiers, SelectedRecord);
            _openBrowser(uri);
            MobileDiagnostics.DismissPendingNotice();
        }
        catch (Exception ex)
        {
            MobileDiagnostics.Error("DIAG", "Opening GitHub issue URL failed", ex);
            ToastRequested?.Invoke(MobileLangResources.Get(MobileLangResourceKind.DiagnosticsExportShareFailed));
        }
    }

    [RelayCommand]
    private void OpenPreview()
    {
        UpdatePreviewAndSummary();
        IsPreviewOpen = true;
    }

    [RelayCommand]
    private void ClosePreview() => IsPreviewOpen = false;

    [RelayCommand]
    private void RequestClearLogs() => IsClearDialogOpen = true;

    [RelayCommand]
    private void ConfirmClearLogs()
    {
        IsClearDialogOpen = false;
        MobileDiagnostics.ClearAllLogs();
        LastExportPath = null;
        Reload();
        ToastRequested?.Invoke(MobileLangResources.Get(MobileLangResourceKind.DiagnosticsCleared));
    }

    private void SelectRecord(DiagnosticRecord record)
    {
        SelectedRecord = record;
        SyncRowSelection();
        UpdatePreviewAndSummary();
        IsPreviewOpen = true;
    }

    private void SyncRowSelection()
    {
        foreach (var row in Records)
        {
            row.IsSelected = SelectedRecord is not null && row.Record.FilePath == SelectedRecord.FilePath;
        }
    }

    private void UpdatePreviewAndSummary()
    {
        if (SelectedRecord is { } record)
        {
            PreviewTitle = $"{record.ReportId} · {record.Category}";
            PreviewText = MobileDiagnostics.ReadRecordContent(record, MaskIdentifiers);
        }
        else
        {
            PreviewTitle = MobileLangResources.Get(MobileLangResourceKind.DiagnosticsRuntimeLogTitle);
            var lines = MobileDiagnostics.ReadRecentRuntimeLines(100, MaskIdentifiers);
            PreviewText = lines.Count > 0
                ? string.Join(Environment.NewLine, lines)
                : MobileLangResources.Get(MobileLangResourceKind.DiagnosticsEmpty);
        }

        CopyableSummary = IsCollectionEnabled
            ? DiagnosticBundleBuilder.BuildTextSummary(_workspace, _account, UserNote, MaskIdentifiers, SelectedRecord)
            : "";
    }

    private void OnDiagnosticsChanged(PendingCrashNotice? _) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(Reload);

    private void OnCollectionEnabledChanged(bool _) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(Reload);

    private static string FormatBytes(long bytes) =>
        bytes < 1024 ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
        : bytes < 1024 * 1024 ? string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB")
        : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.##} MB");

    public void Dispose()
    {
        MobileDiagnostics.PendingNoticeChanged -= OnDiagnosticsChanged;
        MobileDiagnostics.CollectionEnabledChanged -= OnCollectionEnabledChanged;
    }
}

/// <summary>One persisted crash or native-exit entry displayed on <see cref="DiagnosticsPage"/>.</summary>
public sealed partial class DiagnosticRecordRowViewModel : ObservableObject
{
    private readonly Action<DiagnosticRecord> _select;

    public DiagnosticRecordRowViewModel(DiagnosticRecord record, Action<DiagnosticRecord> select)
    {
        Record = record;
        _select = select;
    }

    public DiagnosticRecord Record { get; }
    public string Title => Record.ReportId;
    public string Subtitle => $"{Record.Timestamp:MM-dd HH:mm:ss} · {Record.Category} · {Record.Summary}";
    public string BadgeText => Record.IsNativeExit
        ? MobileLangResources.Get(MobileLangResourceKind.DiagnosticsBadgeNative)
        : Record.IsFatal
            ? MobileLangResources.Get(MobileLangResourceKind.DiagnosticsBadgeCrash)
            : MobileLangResources.Get(MobileLangResourceKind.DiagnosticsBadgeRecovered);
    public BadgeTone BadgeTone => Record.IsNativeExit
        ? BadgeTone.Danger
        : Record.IsFatal
            ? BadgeTone.Danger
            : BadgeTone.Warning;

    [ObservableProperty] public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Select() => _select(Record);
}
