using System.ComponentModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Account;
using Octopus.Mobile.Common;
using Octopus.Mobile.Diagnostics;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Updates;
using Octopus.Mobile.Voice;
using Octopus.Mobile.Workspace;
using TKCode.Arc;

namespace Octopus.Mobile.Settings;

/// <summary>
/// Unified mobile Settings Center pushed from the "Me" tab's Settings button:
/// appearance &amp; theme, background anti-freeze protection, terminal &amp; voice input, account/server &amp; hosts, and updates/about.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly IRemoteWorkspace _workspace;
    private readonly IAccountSession _account;
    private readonly INavigator _navigator;
    private readonly Action<Uri> _openBrowser;

    public SettingsViewModel(
        IRemoteWorkspace workspace,
        IAccountSession account,
        INavigator navigator,
        Action<Uri>? openBrowser = null,
        MobileUpdater? updater = null)
    {
        _workspace = workspace;
        _account = account;
        _navigator = navigator;
        _openBrowser = openBrowser ?? (_ => { });
        Updater = updater ?? MobileUpdater.Unsupported;

        ThemeLabels =
        [
            MobileLangResources.Get(MobileLangResourceKind.SettingsThemeSystem),
            MobileLangResources.Get(MobileLangResourceKind.SettingsThemeDark),
            MobileLangResources.Get(MobileLangResourceKind.SettingsThemeLight),
        ];

        SyncFromPreferences();
        MobilePreferences.Current.Changed += OnPreferencesChanged;
        MobileHost.Voice.Changed += OnVoiceChanged;
        MobileDiagnostics.CollectionEnabledChanged += OnDiagnosticsChanged;
        Updater.PropertyChanged += OnUpdaterChanged;
    }

    public MobileUpdater Updater { get; }
    public IReadOnlyList<string> ThemeLabels { get; }

    [ObservableProperty] public partial string SearchQuery { get; set; } = "";
    [ObservableProperty] public partial int SelectedThemeIndex { get; set; }
    [ObservableProperty] public partial bool CompactDensity { get; set; }
    [ObservableProperty] public partial bool SuspendStreamInBackground { get; set; }
    [ObservableProperty] public partial bool SnapshotResyncOnResume { get; set; }
    [ObservableProperty] public partial double TerminalFontSize { get; set; }
    [ObservableProperty] public partial bool TerminalPasteConfirm { get; set; }
    [ObservableProperty] public partial string? ProbeStatusMessage { get; set; }
    [ObservableProperty] public partial bool IsProbing { get; set; }

    public bool HasProbeStatus => !string.IsNullOrWhiteSpace(ProbeStatusMessage);
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchQuery);
    public string TerminalFontSizeText => $"{TerminalFontSize:0.#} pt";
    public string TerminalFontPreviewText => MobileLangResources.Format(MobileLangResourceKind.SettingsTerminalFontPreview, TerminalFontSizeText);
    public bool CanSmallerFont => TerminalFontSize > 8;
    public bool CanLargerFont => TerminalFontSize < 20;
    public bool IsVoiceSupported => MobileHost.Voice.IsSupported;
    public string VoiceEngineName => VoiceService.NameOf(MobileHost.Voice.Settings.Engine);

    public bool IsSignedIn => _account.IsSignedIn;
    public bool IsLocalOnly => _account.IsLocalOnly;
    public bool IsSignedOut => !_account.IsSignedIn;
    public string AccountName => IsSignedIn
        ? _account.Name ?? MobileLangResources.Get(MobileLangResourceKind.Account)
        : MobileLangResources.Get(MobileLangResourceKind.NotSignedIn);
    public string? ActiveServerUrl => !string.IsNullOrWhiteSpace(_account.Server) ? _account.Server : _account.OfficialServer;
    public string ServerHostDisplay => WelcomeViewModel.FormatHost(ActiveServerUrl) ?? "octoterm.si";
    public Uri? AccountWebUri
    {
        get
        {
            if (!IsSignedIn || string.IsNullOrWhiteSpace(ActiveServerUrl)) return null;
            var raw = ActiveServerUrl.Trim();
            if (!raw.Contains("://", StringComparison.Ordinal)) raw = "https://" + raw;
            return Uri.TryCreate(raw.TrimEnd('/') + "/account", UriKind.Absolute, out var uri) ? uri : null;
        }
    }
    public bool HasAccountWeb => AccountWebUri is not null;

    public string OnlineHostsSummary => MobileLangResources.Format(
        MobileLangResourceKind.SettingsOnlineHostsSummary,
        _workspace.Hosts.Count(h => h.IsOnline && h.IsPaired),
        _workspace.Hosts.Count);

    public string AppVersionText =>
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "1.0.0";

    public BadgeTone DiagnosticsBadgeTone => MobileDiagnostics.IsCollectionEnabled ? BadgeTone.Success : BadgeTone.Neutral;
    public string DiagnosticsBadgeText => MobileLangResources.Get(
        MobileDiagnostics.IsCollectionEnabled ? MobileLangResourceKind.DiagnosticsStatusOn : MobileLangResourceKind.DiagnosticsStatusOff);

    // Instant section search filtering
    public bool ShowAppearanceSection => MatchesSearch("外观", "主题", "深色", "浅色", "紧凑", "density", "theme", "dark", "light", "appearance");
    public bool ShowPerformanceSection => MatchesSearch("后台", "卡顿", "卡死", "快照", "流式", "重连", "探测", "background", "freeze", "snapshot", "stream", "reconnect", "performance");
    public bool ShowTerminalSection => MatchesSearch("终端", "字号", "粘贴", "语音", "输入", "terminal", "font", "paste", "voice", "input");
    public bool ShowAccountSection => MatchesSearch("账号", "设备", "主机", "服务器", "配对", "密码", "登录", "退出", "account", "server", "host", "device", "pair");
    public bool ShowAboutSection => MatchesSearch("关于", "版本", "更新", "重置", "默认", "日志", "崩溃", "异常", "诊断", "反馈", "about", "version", "update", "reset", "default", "log", "crash", "diag", "feedback", "bug");
    public bool HasNoSearchResults =>
        HasSearchQuery
        && !ShowAppearanceSection
        && !ShowPerformanceSection
        && !ShowTerminalSection
        && !ShowAccountSection
        && !ShowAboutSection;

    partial void OnSearchQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchQuery));
        OnPropertyChanged(nameof(ShowAppearanceSection));
        OnPropertyChanged(nameof(ShowPerformanceSection));
        OnPropertyChanged(nameof(ShowTerminalSection));
        OnPropertyChanged(nameof(ShowAccountSection));
        OnPropertyChanged(nameof(ShowAboutSection));
        OnPropertyChanged(nameof(HasNoSearchResults));
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        var mode = value switch
        {
            1 => MobileThemeMode.Dark,
            2 => MobileThemeMode.Light,
            _ => MobileThemeMode.System,
        };
        MobilePreferences.Current.ThemeMode = mode;
    }

    partial void OnCompactDensityChanged(bool value) => MobilePreferences.Current.CompactDensity = value;
    partial void OnSuspendStreamInBackgroundChanged(bool value) => MobilePreferences.Current.SuspendStreamInBackground = value;
    partial void OnSnapshotResyncOnResumeChanged(bool value) => MobilePreferences.Current.SnapshotResyncOnResume = value;
    partial void OnTerminalPasteConfirmChanged(bool value) => MobilePreferences.Current.TerminalPasteConfirm = value;

    partial void OnTerminalFontSizeChanged(double value)
    {
        MobilePreferences.Current.TerminalFontSize = value;
        OnPropertyChanged(nameof(TerminalFontSizeText));
        OnPropertyChanged(nameof(TerminalFontPreviewText));
        OnPropertyChanged(nameof(CanSmallerFont));
        OnPropertyChanged(nameof(CanLargerFont));
    }

    partial void OnProbeStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasProbeStatus));

    [RelayCommand]
    private void Back() => _navigator.Back();

    [RelayCommand]
    private void ClearSearch() => SearchQuery = "";

    [RelayCommand]
    private void ResetDefaults()
    {
        MobilePreferences.Current.ThemeMode = MobileThemeMode.System;
        MobilePreferences.Current.CompactDensity = true;
        MobilePreferences.Current.SuspendStreamInBackground = true;
        MobilePreferences.Current.SnapshotResyncOnResume = true;
        MobilePreferences.Current.TerminalFontSize = 12;
        MobilePreferences.Current.TerminalPasteConfirm = true;
        ProbeStatusMessage = MobileLangResources.Get(MobileLangResourceKind.SettingsResetDone);
    }

    [RelayCommand]
    private void SmallerFont() => TerminalFontSize = Math.Max(8, TerminalFontSize - 1);

    [RelayCommand]
    private void LargerFont() => TerminalFontSize = Math.Min(20, TerminalFontSize + 1);

    [RelayCommand]
    private async Task ProbeHostsNowAsync()
    {
        if (IsProbing) return;
        IsProbing = true;
        ProbeStatusMessage = null;
        try
        {
            _workspace.OnAppForegrounded();
            await _workspace.RefreshAsync();
            ProbeStatusMessage = MobileLangResources.Get(MobileLangResourceKind.SettingsProbeDone);
            OnPropertyChanged(nameof(OnlineHostsSummary));
        }
        finally
        {
            IsProbing = false;
        }
    }

    [RelayCommand]
    private void OpenVoiceSettings() => _navigator.ShowVoiceSettings();

    [RelayCommand]
    private void OpenDiagnostics() => _navigator.ShowDiagnostics();

    [RelayCommand]
    private void OpenAddHost() => _navigator.ShowAddHost();

    [RelayCommand]
    private void OpenCustomServer() => _navigator.ShowCustomServer();

    [RelayCommand]
    private void OpenAccountWeb()
    {
        if (AccountWebUri is { } uri) _openBrowser(uri);
    }

    [RelayCommand]
    private void SignIn() => _navigator.ShowSignIn();

    [RelayCommand]
    private void SignOut() => _account.SignOut();

    [RelayCommand]
    private void LeaveLocal() => _account.LeaveLocalOnly();

    private void SyncFromPreferences()
    {
        var prefs = MobilePreferences.Current;
        SelectedThemeIndex = (int)prefs.ThemeMode;
        CompactDensity = prefs.CompactDensity;
        SuspendStreamInBackground = prefs.SuspendStreamInBackground;
        SnapshotResyncOnResume = prefs.SnapshotResyncOnResume;
        TerminalFontSize = prefs.TerminalFontSize;
        TerminalPasteConfirm = prefs.TerminalPasteConfirm;
    }

    private void OnPreferencesChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(SyncFromPreferences);
    private void OnVoiceChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(VoiceEngineName)));
    private void OnDiagnosticsChanged(bool _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(DiagnosticsBadgeTone));
        OnPropertyChanged(nameof(DiagnosticsBadgeText));
    });
    private void OnUpdaterChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(Updater));

    private bool MatchesSearch(params string[] keywords)
    {
        var q = SearchQuery.Trim();
        if (q.Length == 0) return true;
        foreach (var kw in keywords)
        {
            if (kw.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public void Dispose()
    {
        MobilePreferences.Current.Changed -= OnPreferencesChanged;
        MobileHost.Voice.Changed -= OnVoiceChanged;
        MobileDiagnostics.CollectionEnabledChanged -= OnDiagnosticsChanged;
        Updater.PropertyChanged -= OnUpdaterChanged;
    }
}
