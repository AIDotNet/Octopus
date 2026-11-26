using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Localization;
using Octopus.App.Remote;
using Octopus.Platform;
using Octopus.Remote.Host;
using Octopus.Remote.Relay;
using Octopus.Remote.Security;
using Octopus.Storage;

namespace Octopus.App.Settings.Remote;

/// <summary>A device that asks to be paired: its name, the number to compare, and the person's two answers.</summary>
public sealed partial class PairingRequestRow(PairingRequest request)
{
    public string Name => request.Device.Name;
    /// <summary>Three digits, a gap, three digits: easier to compare with the number on the device.</summary>
    public string Sas => request.Sas.Length == 6 ? request.Sas[..3] + " " + request.Sas[3..] : request.Sas;
    public string DeviceId => request.DeviceId;

    [RelayCommand] private void Allow() => request.Allow();
    [RelayCommand] private void Refuse() => request.Refuse();
}

/// <summary>A paired device in the list.</summary>
/// <param name="ViaPassword">It got in with this computer's access password (design/16), not by a person comparing numbers.</param>
public sealed record PairedDeviceRow(string Id, string Name, string Kind, bool IsOnline, string Details, bool ViaPassword = false);

/// <summary>
/// The "Remote access" page (design/12-远程操作.md §7.1): the name other devices see, local network access, pairing a
/// new device, and the devices that are paired.
/// </summary>
public sealed partial class RemoteAccessSettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>How long new devices may ask after "Start pairing".</summary>
    public static readonly TimeSpan PairingDuration = TimeSpan.FromMinutes(5);

    private readonly ISettingsStore _settings;
    private readonly RemoteHostRuntime _runtime;
    private readonly RelayAccess _relay;
    private readonly IFileOpener? _opener;
    private RemoteHostService? _service;

    /// <param name="opener">Opens the relay's sign-in page in the browser; without it the page shows only the code.</param>
    public RemoteAccessSettingsViewModel(ISettingsStore settings, RemoteHostRuntime runtime, RelayAccess relay, IFileOpener? opener = null)
    {
        _settings = settings;
        _runtime = runtime;
        _relay = relay;
        _opener = opener;
        RelayUrl = relay.Url;
        _ = LoadRelayAsync();
        runtime.Changed += OnChanged;
        relay.Changed += OnChanged;
        Attach(runtime.Service);
        Refresh();
    }

    public ObservableCollection<PairingRequestRow> Requests { get; } = [];
    public ObservableCollection<PairedDeviceRow> Devices { get; } = [];

    [ObservableProperty] public partial bool IsListening { get; private set; }
    [ObservableProperty] public partial string AddressesText { get; private set; } = "";
    [ObservableProperty] public partial string? ErrorText { get; private set; }
    [ObservableProperty] public partial bool IsPairing { get; private set; }
    [ObservableProperty] public partial string? PairingLink { get; private set; }
    [ObservableProperty] public partial QrCodeMatrix? PairingQrCode { get; private set; }
    [ObservableProperty] public partial bool HasDevices { get; private set; }
    [ObservableProperty] public partial bool HasRequests { get; private set; }

    private string? _encodedQrLink;

    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public bool HasPairingLink => !string.IsNullOrEmpty(PairingLink);
    public bool HasPairingQrCode => PairingQrCode is not null;
    public bool IsPairingLinkUsed => IsPairing && !HasPairingLink;
    public bool IsWaitingForRequest => IsPairing && !HasRequests;
    /// <summary>Remote access is on but closing the window would quit the app: the page says how to keep it running.</summary>
    public bool ShowTrayHint => LanEnabled && !_settings.Current.Tray.CloseToTray;

    public string DeviceName
    {
        get => _settings.Current.Remote.DeviceName ?? "";
        set
        {
            var name = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (name == _settings.Current.Remote.DeviceName) return;
            _settings.Update(s => s.Remote.DeviceName = name);
            OnPropertyChanged();
        }
    }

    /// <summary>The computer's own name, shown while the field is empty.</summary>
    public string DeviceNamePlaceholder => Environment.MachineName;

    public bool LanEnabled
    {
        get => _settings.Current.Remote.LanEnabled;
        set
        {
            if (value == _settings.Current.Remote.LanEnabled) return;
            _settings.Update(s => s.Remote.LanEnabled = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowTrayHint));
            _ = ApplyAsync();
        }
    }

    // ---- the relay (design/12 §8) ----

    /// <summary>The relay's address as typed; kept by <see cref="SaveRelayCommand"/> and by signing in.</summary>
    [ObservableProperty] public partial string RelayUrl { get; set; } = "";
    /// <summary>Optional Access Key for browser-free sign-in on a self-hosted Octopus Relay; never persisted in settings.</summary>
    [ObservableProperty] public partial string RelayAccessKey { get; set; } = "";
    [ObservableProperty] public partial string RelayStatus { get; private set; } = "";
    [ObservableProperty] public partial bool IsRelayOnline { get; private set; }
    [ObservableProperty] public partial string? RelayErrorText { get; private set; }
    /// <summary>Who this computer is signed in as at the relay, or that it is not.</summary>
    [ObservableProperty] public partial string RelayAccountText { get; private set; } = "";
    [ObservableProperty] public partial bool IsRelaySignedIn { get; private set; }
    /// <summary>A sign-in waits for its code to be confirmed in the browser.</summary>
    [ObservableProperty] public partial bool IsRelaySigningIn { get; private set; }
    /// <summary>The code the person compares with the one in the browser.</summary>
    [ObservableProperty] public partial string? RelayLoginCode { get; private set; }
    /// <summary>The relay ended this computer's sign-in and nobody signed in again.</summary>
    [ObservableProperty] public partial bool RelaySignInEnded { get; private set; }

    private CancellationTokenSource? _relaySignIn;
    private string? _relayLoginPage;

    public bool HasRelayError => !string.IsNullOrEmpty(RelayErrorText);
    public bool CanSignInRelay => !IsRelaySignedIn && !IsRelaySigningIn;
    /// <summary>The address typed is plain http to another machine: the sign-in would travel unencrypted.</summary>
    public bool IsRelayInsecure => Octopus.Remote.Relay.RelayAccountClient.ParseUrl(RelayUrl) is { Scheme: "http", IsLoopback: false };

    partial void OnRelayUrlChanged(string value)
    {
        if (RelayConfigLink.TryParse(value) is { } link)
        {
            RelayUrl = link.Url.GetLeftPart(UriPartial.Authority);
            if (!string.IsNullOrEmpty(link.AccessKey))
            {
                RelayAccessKey = link.AccessKey;
            }
            return;
        }

        OnPropertyChanged(nameof(IsRelayInsecure));
    }
    partial void OnRelayErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasRelayError));
    partial void OnIsRelaySignedInChanged(bool value) => OnPropertyChanged(nameof(CanSignInRelay));
    partial void OnIsRelaySigningInChanged(bool value) => OnPropertyChanged(nameof(CanSignInRelay));

    public bool RelayHostEnabled
    {
        get => _settings.Current.Remote.RelayHostEnabled;
        set
        {
            if (value == _settings.Current.Remote.RelayHostEnabled) return;
            _settings.Update(s => s.Remote.RelayHostEnabled = value);
            OnPropertyChanged();
            _ = ApplyAsync();
        }
    }

    /// <summary>Keeps the address; the connection to the relay follows. Another relay means signing in there.</summary>
    [RelayCommand]
    private Task SaveRelayAsync() => Applied = SaveRelayCoreAsync();

    private async Task SaveRelayCoreAsync()
    {
        RelayErrorText = null;
        CancelRelaySignIn();
        await _relay.SetUrlAsync(RelayUrl);
        RelayUrl = _relay.Url;
        await ApplyCoreAsync();
    }

    /// <summary>
    /// Signs this computer in at the relay: the code is shown here, the relay's page is opened in the browser, and the
    /// page waits until the person confirmed there (or refused, or gave up).
    /// </summary>
    [RelayCommand]
    private Task SignInRelayAsync() => RelaySignIn = SignInRelayCoreAsync();

    /// <summary>The sign-in under way (or the last one), for tests to wait on.</summary>
    internal Task RelaySignIn { get; private set; } = Task.CompletedTask;

    private async Task SignInRelayCoreAsync()
    {
        if (IsRelaySigningIn) return;
        RelayErrorText = null;
        var signIn = _relaySignIn = new CancellationTokenSource();
        IsRelaySigningIn = true;
        try
        {
            // What is typed is what is signed in to, whether or not "Apply" was pressed first.
            var targetUrl = RelayUrl;
            var accessKey = RelayAccessKey.Trim();
            if (RelayConfigLink.TryParse(targetUrl) is { } configLink)
            {
                targetUrl = configLink.Url.GetLeftPart(UriPartial.Authority);
                if (!string.IsNullOrEmpty(configLink.AccessKey))
                {
                    accessKey = configLink.AccessKey;
                }
            }

            await _relay.SetUrlAsync(targetUrl);
            if (Octopus.Remote.Relay.RelayAccountClient.ParseUrl(_relay.Url) is null)
            {
                RelayErrorText = AppLangResources.Get(AppLangResourceKind.SettingsRemoteRelayStateNoUrl);
                return;
            }
            if (!string.IsNullOrEmpty(accessKey))
            {
                await _relay.SignInWithKeyAsync(accessKey, _runtime.DeviceName, signIn.Token);
                RelayAccessKey = "";
                return;
            }
            var login = await _relay.BeginSignInAsync(_runtime.DeviceName, signIn.Token);
            RelayLoginCode = login.UserCode;
            _relayLoginPage = login.VerificationUriComplete.ToString();
            _opener?.OpenUrl(_relayLoginPage);
            switch (await _relay.CompleteSignInAsync(login, signIn.Token))
            {
                case RelaySignInResult.Denied:
                    RelayErrorText = AppLangResources.Get(AppLangResourceKind.SettingsRemoteRelayDenied);
                    break;
                case RelaySignInResult.Expired:
                    RelayErrorText = AppLangResources.Get(AppLangResourceKind.SettingsRemoteRelayExpired);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException e)
        {
            RelayErrorText = string.Format(AppLangResources.Get(AppLangResourceKind.SettingsRemoteRelaySignInFailed), e.Message);
        }
        finally
        {
            if (ReferenceEquals(_relaySignIn, signIn)) _relaySignIn = null;
            signIn.Dispose();
            (RelayLoginCode, _relayLoginPage, IsRelaySigningIn) = (null, null, false);
        }
        await ApplyCoreAsync();
    }

    /// <summary>Opens the relay's page again (the browser window was closed, or never came up).</summary>
    [RelayCommand]
    private void OpenRelayLogin()
    {
        if (_relayLoginPage is { } page) _opener?.OpenUrl(page);
    }

    [RelayCommand]
    private void CancelRelaySignIn()
    {
        try { _relaySignIn?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    [RelayCommand]
    private Task SignOutRelayAsync() => Applied = SignOutRelayCoreAsync();

    private async Task SignOutRelayCoreAsync()
    {
        RelayErrorText = null;
        await _relay.SignOutAsync();
        await ApplyCoreAsync();
    }

    private async Task LoadRelayAsync()
    {
        await _relay.LoadAsync();
        OnChanged();
    }

    public string Port
    {
        get => _settings.Current.Remote.LanPort.ToString();
        set
        {
            if (!int.TryParse(value, out var port) || port is < 0 or > 65535 || port == _settings.Current.Remote.LanPort) return;
            _settings.Update(s => s.Remote.LanPort = port);
            OnPropertyChanged();
        }
    }

    /// <summary>The last change of the listening state, for tests to wait on.</summary>
    internal Task Applied { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private async Task StartPairingAsync()
    {
        var service = await _runtime.GetAsync();
        Attach(service);
        service.OpenPairing(PairingDuration);
        Refresh();
    }

    [RelayCommand]
    private void StopPairing()
    {
        _service?.ClosePairing();
        Refresh();
    }

    [RelayCommand]
    private void RefreshPairingLink()
    {
        if (_service?.RefreshPairingToken() == true) Refresh();
    }

    [RelayCommand]
    private async Task RevokeAsync(PairedDeviceRow? row)
    {
        if (row is null || _service is null) return;
        await _service.RevokeAsync(row.Id);
        Refresh();
    }

    public void Dispose()
    {
        CancelRelaySignIn();
        _relay.Changed -= OnChanged;
        _runtime.Changed -= OnChanged;
        if (_service is not null)
        {
            _service.Changed -= OnChanged;
            _service.Devices.Changed -= OnChanged;
        }
    }

    private Task ApplyAsync() => Applied = ApplyCoreAsync();

    private async Task ApplyCoreAsync()
    {
        await _runtime.ApplyAsync();
        Attach(_runtime.Service);
        Refresh();
    }

    private void Attach(RemoteHostService? service)
    {
        if (service is null || ReferenceEquals(service, _service)) return;
        _service = service;
        service.Changed += OnChanged;
        service.Devices.Changed += OnChanged;
    }

    // The service reports from network threads.
    private void OnChanged()
    {
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh);
    }

    private void Refresh()
    {
        Attach(_runtime.Service);
        var service = _service;
        IsListening = service?.LanPort is not null;
        AddressesText = IsListening ? string.Join("\n", _runtime.LanAddresses()) : AppLangResources.Get(AppLangResourceKind.SettingsRemoteNotListening);
        ErrorText = _runtime.ListenError is { } error
            ? string.Format(AppLangResources.Get(AppLangResourceKind.SettingsRemoteListenFailed), _settings.Current.Remote.LanPort, error)
            : null;

        var window = service?.Pairing;
        IsPairing = window is not null;
        // The link carries every way to this computer: its local addresses and, while registered there, the relay.
        PairingLink = window is null ? null : service!.CreatePairingLink(_runtime.RelayAddress is { } throughRelay ? [.. _runtime.LanAddresses(), throughRelay] : _runtime.LanAddresses())?.ToString();
        if (!string.Equals(_encodedQrLink, PairingLink, StringComparison.Ordinal))
        {
            _encodedQrLink = PairingLink;
            PairingQrCode = string.IsNullOrEmpty(PairingLink) ? null : QrCodeEncoder.Encode(PairingLink, QrErrorCorrectionLevel.Medium);
        }

        IsRelayOnline = _runtime.RelayState == Octopus.Remote.Relay.RelayLinkState.Online;
        RelayStatus = _runtime.RelayState switch
        {
            Octopus.Remote.Relay.RelayLinkState.Online => AppLangResources.Get(AppLangResourceKind.SettingsRemoteRelayStateOnline),
            Octopus.Remote.Relay.RelayLinkState.Connecting => AppLangResources.Get(AppLangResourceKind.SettingsRemoteRelayStateConnecting),
            Octopus.Remote.Relay.RelayLinkState.Failed => string.Format(AppLangResources.Get(AppLangResourceKind.SettingsRemoteRelayStateFailed), _runtime.RelayError),
            _ => AppLangResources.Get(!RelayHostEnabled ? AppLangResourceKind.SettingsRemoteRelayStateOff
                : Octopus.Remote.Relay.RelayAccountClient.ParseUrl(_relay.Url) is null ? AppLangResourceKind.SettingsRemoteRelayStateNoUrl
                : _relay.Account is null ? AppLangResourceKind.SettingsRemoteRelayStateSignIn
                : AppLangResourceKind.SettingsRemoteRelayStateOff),
        };
        IsRelaySignedIn = _relay.Account is not null;
        RelaySignInEnded = _relay.SignInEnded && !IsRelaySignedIn;
        RelayAccountText = _relay.Account is { } account
            ? string.IsNullOrEmpty(account.Email) || account.Email == account.Name ? account.Name : $"{account.Name} · {account.Email}"
            : AppLangResources.Get(AppLangResourceKind.SettingsRemoteRelaySignedOut);
        var requests = service?.PairingRequests ?? [];
        if (!requests.Select(r => r.DeviceId + r.Sas).SequenceEqual(Requests.Select(r => r.DeviceId + r.Sas.Replace(" ", ""))))
        {
            Requests.Clear();
            foreach (var request in requests) Requests.Add(new PairingRequestRow(request));
        }
        HasRequests = Requests.Count > 0;

        var online = (service?.Connections ?? []).Select(c => c.DeviceId).ToHashSet();
        var rows = (service?.Devices.Peers ?? []).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(peer => new PairedDeviceRow(
            peer.Id,
            peer.Name,
            AppLangResources.Get(peer.Kind == DeviceKind.Phone ? AppLangResourceKind.SettingsRemoteKindPhone : AppLangResourceKind.SettingsRemoteKindDesktop),
            online.Contains(peer.Id),
            online.Contains(peer.Id)
                ? AppLangResources.Get(AppLangResourceKind.SettingsRemoteOnline)
                : peer.LastSeenAt is { } seen
                    ? string.Format(AppLangResources.Get(AppLangResourceKind.SettingsRemoteLastSeen), seen.LocalDateTime.ToString("yyyy-MM-dd HH:mm"))
                    : "",
            peer.Via == PairedVia.Password)).ToList();
        if (!rows.SequenceEqual(Devices))
        {
            Devices.Clear();
            foreach (var row in rows) Devices.Add(row);
        }
        HasDevices = Devices.Count > 0;
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasPairingLink));
        OnPropertyChanged(nameof(HasPairingQrCode));
        OnPropertyChanged(nameof(IsPairingLinkUsed));
        OnPropertyChanged(nameof(IsWaitingForRequest));
        OnPropertyChanged(nameof(ShowTrayHint));
    }
}
