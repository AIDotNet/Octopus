using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Localization;
using Octopus.App.Remote;
using Octopus.Remote.Relay;
using Octopus.Remote.Security;
using Octopus.Storage;

namespace Octopus.App.Settings.Remote;

/// <summary>What a device of the account is to this computer.</summary>
public enum AccountDeviceState
{
    /// <summary>This computer itself.</summary>
    Self,
    /// <summary>A phone: it reaches computers, nobody reaches it.</summary>
    Phone,
    /// <summary>A computer this one may already reach (paired, by numbers, a link or its password).</summary>
    Authorized,
    /// <summary>Online and takes its access password.</summary>
    NeedsPassword,
    /// <summary>Online but does not take passwords (an older version, or turned off there): pairing by numbers or a link.</summary>
    NeedsPairing,
    /// <summary>Not connected to the relay now.</summary>
    Offline,
}

/// <summary>One device of the account in the list, with the inline password entry of a computer that needs one.</summary>
public sealed partial class AccountDeviceRow : ObservableObject
{
    private readonly AccountDevicesSettingsViewModel _owner;

    internal AccountDeviceRow(AccountDevicesSettingsViewModel owner, RelayDevice device, AccountDeviceState state, string details)
    {
        _owner = owner;
        Device = device;
        State = state;
        Details = details;
    }

    internal RelayDevice Device { get; }
    public string Id => Device.Id;
    public string Name => Device.Name;
    public bool IsPhone => Device.Kind == "phone";
    public string Details { get; }
    public AccountDeviceState State { get; }

    public bool IsSelf => State == AccountDeviceState.Self;
    public bool IsOnline => Device.Online;
    public bool IsOffline => State == AccountDeviceState.Offline;
    public bool IsAuthorized => State == AccountDeviceState.Authorized;
    public bool NeedsPassword => State == AccountDeviceState.NeedsPassword;
    public bool NeedsPairing => State == AccountDeviceState.NeedsPairing;
    public double RowOpacity => IsOffline ? 0.55 : 1;
    public bool HasDetails => Details.Length > 0;
    /// <summary>The button that opens the password entry, while it is closed.</summary>
    public bool ShowEnterButton => NeedsPassword && !IsEntering;

    partial void OnIsEnteringChanged(bool value) => OnPropertyChanged(nameof(ShowEnterButton));

    /// <summary>The password entry under the row is open.</summary>
    [ObservableProperty] public partial bool IsEntering { get; set; }
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial bool IsConnecting { get; private set; }
    [ObservableProperty] public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    /// <summary>Eight letters and digits typed (dashes and spaces aside), and nothing under way.</summary>
    public bool CanSubmit => !IsConnecting && AccessPassword.Normalize(Password) is not null;

    partial void OnPasswordChanged(string value)
    {
        ErrorText = null;
        OnPropertyChanged(nameof(CanSubmit));
    }

    partial void OnIsConnectingChanged(bool value) => OnPropertyChanged(nameof(CanSubmit));
    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    /// <summary>The attempt under way (or the last one), for tests to wait on.</summary>
    internal Task Connecting { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private void BeginEntering()
    {
        (Password, ErrorText, IsEntering) = ("", null, true);
    }

    [RelayCommand]
    private void CancelEntering()
    {
        if (!IsConnecting) (IsEntering, Password, ErrorText) = (false, "", null);
    }

    [RelayCommand]
    private Task SubmitAsync() => Connecting = SubmitCoreAsync();

    private async Task SubmitCoreAsync()
    {
        if (!CanSubmit) return;
        IsConnecting = true;
        ErrorText = null;
        try
        {
            ErrorText = await _owner.AccessAsync(this, Password);
        }
        finally
        {
            IsConnecting = false;
        }
        if (ErrorText is null) IsEntering = false;
    }
}

/// <summary>
/// The "Account &amp; devices" page (design/16-账号设备与访问密码.md): this computer's access password, and every device
/// signed in to the account — online or not — with getting into a computer of the account by its password.
/// </summary>
public sealed partial class AccountDevicesSettingsViewModel : ObservableObject, IDisposable
{
    private const string Mask = "••••-••••";

    private readonly ISettingsStore _settings;
    private readonly RemoteHostRuntime _host;
    private readonly RemoteClientRuntime _client;
    private readonly RelayAccess _relay;
    private string? _password;
    private bool _disposed;

    public AccountDevicesSettingsViewModel(ISettingsStore settings, RemoteHostRuntime host, RemoteClientRuntime client, RelayAccess relay)
    {
        _settings = settings;
        _host = host;
        _client = client;
        _relay = relay;
        host.Changed += OnChanged;
        relay.Changed += OnRelayChanged;
        client.Hosts.Changed += OnHostsChanged;
        Loaded = LoadAsync();
    }

    /// <summary>The first load (password and device list), for tests to wait on.</summary>
    internal Task Loaded { get; }

    // ---- account ----

    [ObservableProperty] public partial bool IsSignedIn { get; private set; }
    [ObservableProperty] public partial string AccountText { get; private set; } = "";

    // ---- this computer's access password ----

    public bool PasswordAccessEnabled
    {
        get => _settings.Current.Remote.PasswordAccessEnabled;
        set
        {
            if (value == _settings.Current.Remote.PasswordAccessEnabled) return;
            _settings.Update(s => s.Remote.PasswordAccessEnabled = value);
            OnPropertyChanged();
            Applied = ApplyAsync();
        }
    }

    /// <summary>"Reachable through the relay": without it, the account's other devices see this computer offline.</summary>
    public bool RelayHostEnabled
    {
        get => _settings.Current.Remote.RelayHostEnabled;
        set
        {
            if (value == _settings.Current.Remote.RelayHostEnabled) return;
            _settings.Update(s => s.Remote.RelayHostEnabled = value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowRelayOffHint));
            Applied = ApplyAsync();
        }
    }

    [ObservableProperty] public partial bool IsPasswordRevealed { get; private set; }
    /// <summary>The password as the page shows it: dots until revealed.</summary>
    [ObservableProperty] public partial string PasswordText { get; private set; } = Mask;
    [ObservableProperty] public partial bool IsEditingPassword { get; private set; }
    [ObservableProperty] public partial string NewPassword { get; set; } = "";
    [ObservableProperty] public partial string? PasswordErrorText { get; private set; }
    /// <summary>A change of password also forgets the devices that came in with the old one.</summary>
    [ObservableProperty] public partial bool RevokeOnChange { get; set; }
    [ObservableProperty] public partial bool IsSuspended { get; private set; }
    [ObservableProperty] public partial bool Copied { get; private set; }
    [ObservableProperty] public partial string PasswordDevicesText { get; private set; } = "";

    public bool HasPasswordError => !string.IsNullOrEmpty(PasswordErrorText);
    public bool ShowRelayOffHint => IsSignedIn && !RelayHostEnabled;
    public string RevealText => AppLangResources.Get(IsPasswordRevealed ? AppLangResourceKind.AccountDevicesHide : AppLangResourceKind.AccountDevicesShow);

    partial void OnPasswordErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasPasswordError));
    partial void OnIsPasswordRevealedChanged(bool value) => OnPropertyChanged(nameof(RevealText));
    partial void OnIsSignedInChanged(bool value) => OnPropertyChanged(nameof(ShowRelayOffHint));
    partial void OnNewPasswordChanged(string value) => PasswordErrorText = null;

    /// <summary>The last change of the setting or the password, for tests to wait on.</summary>
    internal Task Applied { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private void ToggleReveal()
    {
        IsPasswordRevealed = !IsPasswordRevealed;
        RefreshPassword();
    }

    /// <summary>What "Copy" puts on the clipboard (the view does the copying): the password as shown when revealed.</summary>
    public string? PasswordToCopy => _password is null ? null : AccessPassword.Format(_password);

    /// <summary>The view copied the password: the button says so for a moment.</summary>
    public void MarkCopied()
    {
        Copied = true;
        _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ => Post(() => Copied = false), TaskScheduler.Default);
    }

    [RelayCommand]
    private void BeginEditPassword() => (NewPassword, PasswordErrorText, IsEditingPassword) = ("", null, true);

    [RelayCommand]
    private void CancelEditPassword() => (IsEditingPassword, NewPassword, PasswordErrorText) = (false, "", null);

    [RelayCommand]
    private Task SavePasswordAsync() => Applied = ChangePasswordAsync(NewPassword);

    [RelayCommand]
    private Task RegeneratePasswordAsync() => Applied = ChangePasswordAsync(null);

    private async Task ChangePasswordAsync(string? typed)
    {
        if (typed is not null && AccessPassword.Normalize(typed) is null)
        {
            PasswordErrorText = AppLangResources.Get(AppLangResourceKind.AccountDevicesPasswordInvalid);
            return;
        }
        _password = await _host.ChangeAccessPasswordAsync(typed, RevokeOnChange);
        (IsEditingPassword, NewPassword, PasswordErrorText) = (false, "", null);
        // A new password is shown once, so the person can pass it on.
        IsPasswordRevealed = true;
        RefreshPassword();
    }

    [RelayCommand]
    private void ResumePasswordAccess()
    {
        _host.ResumePasswordAccess();
        RefreshPassword();
    }

    // ---- the account's devices ----

    public ObservableCollection<AccountDeviceRow> Devices { get; } = [];
    [ObservableProperty] public partial bool IsLoadingDevices { get; private set; }
    [ObservableProperty] public partial string? DevicesErrorText { get; private set; }
    [ObservableProperty] public partial bool HasDevices { get; private set; }

    public bool HasDevicesError => !string.IsNullOrEmpty(DevicesErrorText);
    partial void OnDevicesErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasDevicesError));

    /// <summary>The last reload of the list, for tests to wait on.</summary>
    internal Task Reloaded { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private Task RefreshDevicesAsync() => Reloaded = LoadDevicesAsync();

    /// <summary>
    /// Gets into a computer of the account with its password. Null when it worked (the computer is now among the remote
    /// hosts), otherwise what to tell the person.
    /// </summary>
    internal async Task<string?> AccessAsync(AccountDeviceRow row, string password)
    {
        try
        {
            await _client.AccessWithPasswordAsync(row.Id, password);
            Reloaded = LoadDevicesAsync();
            return null;
        }
        catch (HandshakeException e)
        {
            return AppLangResources.Get(e.Status switch
            {
                HandshakeStatus.WrongPassword => AppLangResourceKind.AccountDevicesWrongPassword,
                HandshakeStatus.PasswordLocked => AppLangResourceKind.AccountDevicesLocked,
                HandshakeStatus.PairingClosed => AppLangResourceKind.AccountDevicesPasswordOff,
                _ when e.Failure == HandshakeFailure.WrongPeer => AppLangResourceKind.AccountDevicesNotVerified,
                _ => AppLangResourceKind.AccountDevicesRefused,
            });
        }
        catch (ArgumentException)
        {
            return AppLangResources.Get(AppLangResourceKind.AccountDevicesPasswordInvalid);
        }
        catch (IOException e)
        {
            return string.Format(AppLangResources.Get(AppLangResourceKind.AccountDevicesUnreachable), e.Message);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _host.Changed -= OnChanged;
        _relay.Changed -= OnRelayChanged;
        _client.Hosts.Changed -= OnHostsChanged;
    }

    private async Task LoadAsync()
    {
        await _relay.LoadAsync();
        RefreshAccount();
        _password = await _host.GetAccessPasswordAsync();
        RefreshPassword();
        await LoadDevicesAsync();
    }

    private async Task ApplyAsync()
    {
        await _host.ApplyAsync();
        RefreshPassword();
    }

    private async Task LoadDevicesAsync()
    {
        if (_relay.Account is null)
        {
            Devices.Clear();
            (HasDevices, DevicesErrorText) = (false, null);
            return;
        }
        IsLoadingDevices = true;
        try
        {
            IReadOnlyList<RelayDevice> devices;
            try
            {
                var endpoint = await _relay.GetAsync() ?? throw new RelayException(RelayFailure.Unauthorized, "signed out");
                try { devices = await RelayAccountClient.ListDevicesAsync(endpoint); }
                catch (RelayException e) when (e.Failure == RelayFailure.Unauthorized)
                {
                    endpoint = await _relay.GetAsync(fresh: true) ?? throw new RelayException(RelayFailure.Unauthorized, e.Message, e);
                    devices = await RelayAccountClient.ListDevicesAsync(endpoint);
                }
            }
            catch (RelayException e)
            {
                DevicesErrorText = string.Format(AppLangResources.Get(AppLangResourceKind.AccountDevicesListFailed), e.Message);
                return;
            }
            DevicesErrorText = null;
            Show(devices);
        }
        finally
        {
            IsLoadingDevices = false;
        }
    }

    private void Show(IReadOnlyList<RelayDevice> devices)
    {
        // This computer first, then the computers that can be reached, then the rest; by name within each.
        var rows = devices.Select(device => (Device: device, State: StateOf(device)))
            .OrderBy(entry => entry.State switch
            {
                AccountDeviceState.Self => 0,
                AccountDeviceState.Authorized or AccountDeviceState.NeedsPassword or AccountDeviceState.NeedsPairing => 1,
                AccountDeviceState.Offline => 2,
                _ => 3,
            })
            .ThenBy(entry => entry.Device.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => new AccountDeviceRow(this, entry.Device, entry.State, DetailsOf(entry.Device)))
            .ToList();
        Devices.Clear();
        foreach (var row in rows) Devices.Add(row);
        HasDevices = Devices.Count > 0;
    }

    private AccountDeviceState StateOf(RelayDevice device) =>
        device.Current ? AccountDeviceState.Self
        : device.Kind == "phone" ? AccountDeviceState.Phone
        : _client.Hosts.Find(device.Id) is not null ? AccountDeviceState.Authorized
        : !device.Online ? AccountDeviceState.Offline
        : device.PasswordAccess ? AccountDeviceState.NeedsPassword
        : AccountDeviceState.NeedsPairing;

    private static string DetailsOf(RelayDevice device)
    {
        // Online is the badge's to say; otherwise when it was last heard of.
        var parts = new List<string>();
        if (device.Platform is { } platform) parts.Add(platform);
        if (!device.Online) parts.Add(string.Format(AppLangResources.Get(AppLangResourceKind.AccountDevicesLastActive), device.LastSeenAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm")));
        return string.Join(" · ", parts);
    }

    private void RefreshAccount()
    {
        IsSignedIn = _relay.Account is not null;
        AccountText = _relay.Account is { } account
            ? string.IsNullOrEmpty(account.Email) || account.Email == account.Name ? account.Name : $"{account.Name} · {account.Email}"
            : AppLangResources.Get(AppLangResourceKind.SettingsRemoteRelaySignedOut);
        OnPropertyChanged(nameof(ShowRelayOffHint));
    }

    private void RefreshPassword()
    {
        PasswordText = _password is null ? Mask : IsPasswordRevealed ? AccessPassword.Format(_password) : Mask;
        IsSuspended = PasswordAccessEnabled && _host.PasswordAccessSuspended;
        var count = _host.Service?.Devices.Peers.Count(peer => peer.Via == PairedVia.Password) ?? 0;
        PasswordDevicesText = string.Format(AppLangResources.Get(AppLangResourceKind.AccountDevicesPasswordDevices), count);
        OnPropertyChanged(nameof(RelayHostEnabled));
        OnPropertyChanged(nameof(ShowRelayOffHint));
    }

    // From the services' threads.
    private void OnChanged() => Post(RefreshPassword);

    private void OnRelayChanged() => Post(() =>
    {
        var was = IsSignedIn;
        RefreshAccount();
        if (was != IsSignedIn) Reloaded = LoadDevicesAsync();
    });

    private void OnHostsChanged() => Post(() =>
    {
        // Who is authorized changed (paired here, or removed on the remote hosts page): the states follow.
        if (Devices.Count > 0 && Devices.Any(row => (_client.Hosts.Find(row.Id) is not null) != (row.State == AccountDeviceState.Authorized) && row.State is not (AccountDeviceState.Self or AccountDeviceState.Phone)))
        {
            Show([.. Devices.Select(row => row.Device)]);
        }
    });

    private void Post(Action action)
    {
        if (_disposed) return;
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(() => { if (!_disposed) action(); });
    }
}
