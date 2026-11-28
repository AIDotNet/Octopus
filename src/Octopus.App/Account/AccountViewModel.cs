using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Localization;
using Octopus.App.Remote;
using Octopus.App.Shell;
using Octopus.Core;
using Octopus.Platform;
using Octopus.Remote.Relay;
using Octopus.Storage;
using TKCode.Arc.Localization;

namespace Octopus.App.Account;

/// <summary>
/// Top-bar account capsule, centered sign-in modal, and signed-in dropdown menu (Scheme B).
/// Shares <see cref="RelayAccess"/> and <see cref="RemoteHostRuntime"/> with the Remote Access settings page so both stay in sync.
/// </summary>
public sealed partial class AccountViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly RelayAccess _relay;
    private readonly RemoteHostRuntime _runtime;
    private readonly ISettingsNavigator _navigator;
    private readonly IFileOpener? _opener;
    private readonly CoWorkIntegrationService? _cowork;
    private CancellationTokenSource? _signIn;
    private string? _loginPage;
    private bool _disposed;
    /// <summary>Ends the loop that keeps this computer's relay sign-in from running out while the app is open.</summary>
    private readonly CancellationTokenSource _keepFresh = new();

    public AccountViewModel(
        ISettingsStore settings,
        RelayAccess relay,
        RemoteHostRuntime runtime,
        ISettingsNavigator navigator,
        IFileOpener? opener = null,
        CoWorkIntegrationService? cowork = null)
    {
        _settings = settings;
        _relay = relay;
        _runtime = runtime;
        _navigator = navigator;
        _opener = opener;
        _cowork = cowork;

        CustomServerUrl = _settings.Current.Remote.RelayUrl ?? "";
        ShowCustomServer = !string.IsNullOrWhiteSpace(CustomServerUrl);

        _relay.Changed += OnExternalChanged;
        _runtime.Changed += OnExternalChanged;
        _cowork?.Changed += OnCoWorkChanged;
        _settings.Changed += OnSettingsChanged;
        ArcLanguage.Changed += OnLanguageChanged;

        Refresh();
        _ = LoadAsync();
    }

    /// <summary>Raised when the signed-in capsule is clicked and the shell should show the account popover.</summary>
    public event EventHandler? MenuRequested;

    /// <summary>Raised when an action inside the account popover completes and the popover should close.</summary>
    public event EventHandler? MenuCloseRequested;

    /// <summary>The ongoing sign-in task (or completed), exposed for tests.</summary>
    internal Task SignInTask { get; private set; } = Task.CompletedTask;

    /// <summary>The last relay apply task triggered from this view model, exposed for tests.</summary>
    internal Task AppliedTask { get; private set; } = Task.CompletedTask;

    [ObservableProperty] public partial bool IsSignedIn { get; private set; }
    [ObservableProperty] public partial bool IsSigningIn { get; private set; }
    [ObservableProperty] public partial bool SignInEnded { get; private set; }
    [ObservableProperty] public partial bool IsRelayOnline { get; private set; }
    [ObservableProperty] public partial bool IsRelayConnecting { get; private set; }
    [ObservableProperty] public partial bool IsSignInDialogOpen { get; set; }
    [ObservableProperty] public partial bool ShowCustomServer { get; set; }
    [ObservableProperty] public partial string CustomServerUrl { get; set; } = "";
    [ObservableProperty] public partial string CustomAccessKey { get; set; } = "";
    [ObservableProperty] public partial bool AutoEnableRelayOnSignIn { get; set; } = true;
    [ObservableProperty] public partial string? UserCode { get; private set; }
    [ObservableProperty] public partial string? ErrorText { get; private set; }
    [ObservableProperty] public partial bool CodeCopied { get; private set; }

    [ObservableProperty] public partial string AccountName { get; private set; } = "";
    [ObservableProperty] public partial string AccountInitial { get; private set; } = "O";
    [ObservableProperty] public partial string AccountMeta { get; private set; } = "";
    [ObservableProperty] public partial string ServerHost { get; private set; } = "octoterm.si";
    [ObservableProperty] public partial string CapsuleLabel { get; private set; } = "";
    [ObservableProperty] public partial string CapsuleTooltip { get; private set; } = "";
    [ObservableProperty] public partial string DialogTitle { get; private set; } = "";
    [ObservableProperty] public partial string DialogSubtitle { get; private set; } = "";
    [ObservableProperty] public partial string PrimaryActionText { get; private set; } = "";
    [ObservableProperty] public partial string CustomServerToggleText { get; private set; } = "";
    [ObservableProperty] public partial string CustomServerHintText { get; private set; } = "";
    [ObservableProperty] public partial string CodeConfirmHintText { get; private set; } = "";
    [ObservableProperty] public partial string RelayBadgeText { get; private set; } = "";
    [ObservableProperty] public partial string RelaySwitchSubtitle { get; private set; } = "";
    [ObservableProperty] public partial string CoWorkMenuLabel { get; private set; } = "CoWork";
    [ObservableProperty] public partial string CoWorkMenuBadge { get; private set; } = "";

    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public bool ShowSignedOutCapsule => !IsSignedIn && !IsSigningIn && !SignInEnded;
    public bool ShowEndedCapsule => !IsSignedIn && !IsSigningIn && SignInEnded;
    public bool ShowIdleModalContent => !IsSigningIn;
    public bool IsRelayOff => IsSignedIn && !IsRelayOnline && !IsRelayConnecting;
    public bool IsCustomServerInsecure => RelayAccountClient.ParseUrl(EffectiveTargetUrl()) is { Scheme: "http", IsLoopback: false };
    public string OfficialServerAddress => _relay.OfficialUrl ?? ServiceEndpoints.DefaultOfficialServiceUrl;

    public bool RelayHostEnabled
    {
        get => _settings.Current.Remote.RelayHostEnabled;
        set
        {
            if (value == _settings.Current.Remote.RelayHostEnabled) return;
            _settings.Update(s => s.Remote.RelayHostEnabled = value);
            OnPropertyChanged();
            AppliedTask = ApplyRuntimeAsync();
        }
    }

    partial void OnCustomServerUrlChanged(string value)
    {
        if (RelayConfigLink.TryParse(value) is { } link)
        {
            CustomServerUrl = link.Url.GetLeftPart(UriPartial.Authority);
            if (!string.IsNullOrEmpty(link.AccessKey))
            {
                CustomAccessKey = link.AccessKey;
            }
            return;
        }

        OnPropertyChanged(nameof(IsCustomServerInsecure));
        RefreshLabels();
    }

    partial void OnShowCustomServerChanged(bool value) => RefreshLabels();

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsSignedInChanged(bool value) => NotifyComputed();

    partial void OnIsSigningInChanged(bool value) => NotifyComputed();

    partial void OnSignInEndedChanged(bool value) => NotifyComputed();

    partial void OnIsRelayOnlineChanged(bool value) => NotifyComputed();

    partial void OnIsRelayConnectingChanged(bool value) => NotifyComputed();

    private void NotifyComputed()
    {
        OnPropertyChanged(nameof(ShowSignedOutCapsule));
        OnPropertyChanged(nameof(ShowEndedCapsule));
        OnPropertyChanged(nameof(ShowIdleModalContent));
        OnPropertyChanged(nameof(IsRelayOff));
    }

    [RelayCommand]
    private void ActivateCapsule()
    {
        if (IsSignedIn)
        {
            MenuRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            ErrorText = null;
            CustomServerUrl = _settings.Current.Remote.RelayUrl ?? "";
            CustomAccessKey = "";
            ShowCustomServer = !string.IsNullOrWhiteSpace(CustomServerUrl);
            RefreshLabels();
            IsSignInDialogOpen = true;
        }
    }

    [RelayCommand]
    private void CloseSignInDialog()
    {
        IsSignInDialogOpen = false;
    }

    [RelayCommand]
    private void ToggleCustomServer()
    {
        ShowCustomServer = !ShowCustomServer;
    }

    [RelayCommand]
    private Task StartSignInAsync() => SignInTask = StartSignInCoreAsync();

    private async Task StartSignInCoreAsync()
    {
        if (IsSigningIn) return;
        ErrorText = null;
        CodeCopied = false;
        var signIn = _signIn = new CancellationTokenSource();
        IsSigningIn = true;
        RefreshLabels();
        try
        {
            var targetUrl = EffectiveTargetUrl();
            var accessKey = ShowCustomServer ? CustomAccessKey.Trim() : "";
            if (RelayConfigLink.TryParse(targetUrl) is { } configLink)
            {
                targetUrl = configLink.Url.GetLeftPart(UriPartial.Authority);
                if (!string.IsNullOrEmpty(configLink.AccessKey))
                {
                    accessKey = configLink.AccessKey;
                }
            }

            if (RelayAccountClient.ParseUrl(targetUrl) is null)
            {
                ErrorText = AppLangResources.Get(AppLangResourceKind.SettingsRemoteRelayStateNoUrl);
                return;
            }

            await _relay.SetUrlAsync(targetUrl);
            if (!string.IsNullOrEmpty(accessKey))
            {
                await _relay.SignInWithKeyAsync(accessKey, _runtime.DeviceName, signIn.Token);
                CustomAccessKey = "";
                if (AutoEnableRelayOnSignIn && !_settings.Current.Remote.RelayHostEnabled)
                {
                    _settings.Update(s => s.Remote.RelayHostEnabled = true);
                }
                IsSignInDialogOpen = false;
                await ApplyRuntimeAsync();
                return;
            }

            var login = await _relay.BeginSignInAsync(_runtime.DeviceName, signIn.Token);
            UserCode = login.UserCode;
            _loginPage = login.VerificationUriComplete.ToString();
            RefreshLabels();
            _opener?.OpenUrl(_loginPage);

            switch (await _relay.CompleteSignInAsync(login, signIn.Token))
            {
                case RelaySignInResult.SignedIn:
                    if (AutoEnableRelayOnSignIn && !_settings.Current.Remote.RelayHostEnabled)
                    {
                        _settings.Update(s => s.Remote.RelayHostEnabled = true);
                    }
                    IsSignInDialogOpen = false;
                    await ApplyRuntimeAsync();
                    break;
                case RelaySignInResult.Denied:
                    ErrorText = AppLangResources.Get(AppLangResourceKind.SettingsRemoteRelayDenied);
                    break;
                case RelaySignInResult.Expired:
                    ErrorText = AppLangResources.Get(AppLangResourceKind.SettingsRemoteRelayExpired);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException e)
        {
            ErrorText = string.Format(AppLangResources.Get(AppLangResourceKind.SettingsRemoteRelaySignInFailed), e.Message);
        }
        finally
        {
            if (ReferenceEquals(_signIn, signIn)) _signIn = null;
            signIn.Dispose();
            UserCode = null;
            _loginPage = null;
            IsSigningIn = false;
            Refresh();
        }
    }

    [RelayCommand]
    private void OpenBrowserAgain()
    {
        if (_loginPage is { } page)
        {
            _opener?.OpenUrl(page);
        }
    }

    [RelayCommand]
    private void CancelSignIn()
    {
        try { _signIn?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void MarkCodeCopied()
    {
        CodeCopied = true;
        _ = ResetCopiedAsync();
    }

    private async Task ResetCopiedAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (!_disposed)
            {
                Dispatcher.UIThread.Post(() => CodeCopied = false);
            }
        }
        catch (Exception)
        {
        }
    }

    [RelayCommand]
    private void OpenAccountWeb()
    {
        MenuCloseRequested?.Invoke(this, EventArgs.Empty);
        var baseUri = RelayAccountClient.ParseUrl(_relay.Url) ?? RelayAccountClient.ParseUrl(OfficialServerAddress);
        if (baseUri is not null)
        {
            _opener?.OpenUrl(new Uri(baseUri, "/account").AbsoluteUri);
        }
    }

    [RelayCommand]
    private void OpenRemoteSettings()
    {
        MenuCloseRequested?.Invoke(this, EventArgs.Empty);
        IsSignInDialogOpen = false;
        _navigator.OpenSettings("remote");
    }

    [RelayCommand]
    private void OpenAccountDevices()
    {
        MenuCloseRequested?.Invoke(this, EventArgs.Empty);
        IsSignInDialogOpen = false;
        _navigator.OpenSettings("account-devices");
    }

    [RelayCommand]
    private void OpenCoWorkSettings()
    {
        MenuCloseRequested?.Invoke(this, EventArgs.Empty);
        IsSignInDialogOpen = false;
        _navigator.OpenSettings("providers");
    }

    [RelayCommand]
    private Task SignOutAsync() => AppliedTask = SignOutCoreAsync();

    private async Task SignOutCoreAsync()
    {
        MenuCloseRequested?.Invoke(this, EventArgs.Empty);
        ErrorText = null;
        await _relay.SignOutAsync();
        await ApplyRuntimeAsync();
        Refresh();
    }

    public void Dispose()
    {
        _disposed = true;
        _keepFresh.Cancel();
        CancelSignIn();
        _relay.Changed -= OnExternalChanged;
        _runtime.Changed -= OnExternalChanged;
        _cowork?.Changed -= OnCoWorkChanged;
        _settings.Changed -= OnSettingsChanged;
        ArcLanguage.Changed -= OnLanguageChanged;
    }

    private void OnCoWorkChanged(object? sender, EventArgs e) => OnExternalChanged();

    private async Task LoadAsync()
    {
        try
        {
            await _relay.LoadAsync();
            OnExternalChanged();
            _ = _relay.KeepFreshAsync(TimeSpan.FromHours(1), _keepFresh.Token);
        }
        catch (Exception)
        {
        }
    }

    private async Task ApplyRuntimeAsync()
    {
        await _runtime.ApplyAsync();
        OnExternalChanged();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => OnExternalChanged();

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e) => OnExternalChanged();

    private void OnExternalChanged()
    {
        if (_disposed) return;
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        var account = _relay.Account;
        IsSignedIn = account is not null;
        SignInEnded = _relay.SignInEnded && !IsSignedIn;
        IsRelayOnline = _runtime.RelayState == RelayLinkState.Online;
        IsRelayConnecting = _runtime.RelayState == RelayLinkState.Connecting;

        AccountName = account?.Name ?? "";
        AccountInitial = string.IsNullOrWhiteSpace(AccountName) ? "O" : char.ToUpperInvariant(AccountName.Trim()[0]).ToString();

        var host = HostOf(_relay.Url) ?? HostOf(OfficialServerAddress) ?? "octoterm.si";
        ServerHost = host;
        AccountMeta = account is null
            ? host
            : string.IsNullOrEmpty(account.Email) || string.Equals(account.Email, account.Name, StringComparison.OrdinalIgnoreCase)
                ? host
                : $"{account.Email} · {host}";

        OnPropertyChanged(nameof(RelayHostEnabled));
        OnPropertyChanged(nameof(IsCustomServerInsecure));
        RefreshLabels();
    }

    private void RefreshLabels()
    {
        var officialHost = HostOf(OfficialServerAddress) ?? "octoterm.si";
        var effectiveUrl = EffectiveTargetUrl();
        var effectiveHost = HostOf(effectiveUrl) ?? officialHost;
        var isCustom = !string.IsNullOrWhiteSpace(CustomServerUrl)
            && RelayAccountClient.ParseUrl(CustomServerUrl) != RelayAccountClient.ParseUrl(OfficialServerAddress);

        RelayBadgeText = IsRelayOnline
            ? AppLangResources.Get(AppLangResourceKind.AccountStatusOnline)
            : IsRelayConnecting
                ? AppLangResources.Get(AppLangResourceKind.AccountStatusConnecting)
                : AppLangResources.Get(AppLangResourceKind.AccountStatusRelayOff);

        RelaySwitchSubtitle = AppLangResources.Get(IsRelayOnline
            ? AppLangResourceKind.AccountRelayToggleOnHint
            : AppLangResourceKind.AccountRelayToggleOffHint);

        if (IsSignedIn)
        {
            CapsuleLabel = AccountName;
            CapsuleTooltip = AppLangResources.Format(AppLangResourceKind.AccountCapsuleTooltipSignedIn, AccountName, ServerHost, RelayBadgeText);
        }
        else if (IsSigningIn)
        {
            CapsuleLabel = string.IsNullOrEmpty(UserCode)
                ? AppLangResources.Get(AppLangResourceKind.AccountSignIn)
                : AppLangResources.Format(AppLangResourceKind.AccountWaitingCode, UserCode);
            CapsuleTooltip = CapsuleLabel;
        }
        else if (SignInEnded)
        {
            CapsuleLabel = AppLangResources.Get(AppLangResourceKind.AccountSignInAgain);
            CapsuleTooltip = AppLangResources.Get(AppLangResourceKind.AccountCapsuleTooltipEnded);
        }
        else
        {
            CapsuleLabel = AppLangResources.Get(AppLangResourceKind.AccountSignIn);
            CapsuleTooltip = AppLangResources.Format(AppLangResourceKind.AccountCapsuleTooltipSignedOut, officialHost);
        }

        DialogTitle = AppLangResources.Get(SignInEnded
            ? AppLangResourceKind.AccountDialogTitleEnded
            : AppLangResourceKind.AccountDialogTitle);
        DialogSubtitle = AppLangResources.Format(AppLangResourceKind.AccountDialogSubtitle, officialHost);
        PrimaryActionText = isCustom
            ? AppLangResources.Format(AppLangResourceKind.AccountPrimarySignInCustom, effectiveHost)
            : AppLangResources.Format(AppLangResourceKind.AccountPrimarySignInOfficial, officialHost);
        CustomServerToggleText = AppLangResources.Get(ShowCustomServer
            ? AppLangResourceKind.AccountHideCustomServer
            : AppLangResourceKind.AccountUseCustomServer);
        CustomServerHintText = AppLangResources.Format(AppLangResourceKind.AccountCustomServerHint, OfficialServerAddress);
        CodeConfirmHintText = AppLangResources.Format(AppLangResourceKind.AccountCodeConfirmHint, $"{effectiveHost}/device");

        var isZh = ArcLanguage.Culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        CoWorkMenuLabel = isZh ? "CoWork 模型额度" : "CoWork Model Quota";
        var cw = _cowork?.Status;
        CoWorkMenuBadge = cw is { Bound: true, Status: "active", Wallet: { } w }
            ? $"${w.AvailableBalance:F2}"
            : cw is { Bound: true }
                ? (isZh ? "已绑定" : "Linked")
                : (isZh ? "未绑定" : "Link");
    }

    private string EffectiveTargetUrl() =>
        string.IsNullOrWhiteSpace(CustomServerUrl) ? OfficialServerAddress : CustomServerUrl.Trim();

    private static string? HostOf(string? raw) =>
        RelayAccountClient.ParseUrl(raw) is { } uri
            ? (uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}")
            : null;
}
