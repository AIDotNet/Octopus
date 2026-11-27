using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Relay;
using Octopus.Remote.Security;

namespace Octopus.Mobile.Account;

/// <summary>
/// Signing in at one service, from asking it for a code to the answer from the browser: the part the welcome page (the
/// official service) and the custom-service page (an address the person typed) share. The shell replaces the page with
/// the host list as soon as the account is signed in.
/// </summary>
public sealed partial class SignInFlow : ObservableObject, IDisposable
{
    private readonly IAccountSession _account;
    private readonly Action<Uri> _openBrowser;
    private SignInChallenge? _challenge;
    private CancellationTokenSource? _starting;
    private CancellationTokenSource? _copiedCts;
    private bool _disposed;

    public SignInFlow(IAccountSession account, Action<Uri> openBrowser)
    {
        _account = account;
        _openBrowser = openBrowser;
        // Signed out by the service, not by the person: said once, where they sign in again.
        ErrorText = account.SignInEnded ? MobileLangResources.Get(MobileLangResourceKind.SignInEnded) : null;
    }

    /// <summary>The code to confirm in the browser; null until the service gave one.</summary>
    [ObservableProperty] public partial string? UserCode { get; private set; }
    /// <summary>The host name of the service this challenge belongs to (e.g. "octoterm.si").</summary>
    [ObservableProperty] public partial string? ServerHost { get; private set; }
    /// <summary>True briefly right after the user copied the code.</summary>
    [ObservableProperty] public partial bool CodeCopied { get; private set; }
    /// <summary>The service is being asked for a code.</summary>
    [ObservableProperty] public partial bool IsStarting { get; private set; }
    [ObservableProperty] public partial string? ErrorText { get; private set; }

    public bool HasCode => UserCode is not null;
    public bool HasServerHost => !string.IsNullOrEmpty(ServerHost);
    /// <summary>Nothing is under way: a sign-in can be started.</summary>
    public bool IsIdle => !IsStarting && !HasCode;
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    partial void OnUserCodeChanged(string? value)
    {
        if (value is null) CodeCopied = false;
        OnPropertyChanged(nameof(HasCode));
        OnPropertyChanged(nameof(IsIdle));
    }

    partial void OnServerHostChanged(string? value) => OnPropertyChanged(nameof(HasServerHost));
    partial void OnIsStartingChanged(bool value) => OnPropertyChanged(nameof(IsIdle));
    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    /// <summary>Briefly flips the copy button label to "Copied".</summary>
    public void MarkCodeCopied()
    {
        _copiedCts?.Cancel();
        _copiedCts?.Dispose();
        var cts = _copiedCts = new CancellationTokenSource();
        CodeCopied = true;
        _ = ResetCopiedAsync(cts);
    }

    private async Task ResetCopiedAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(2000, cts.Token);
            if (!_disposed && ReferenceEquals(_copiedCts, cts))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && ReferenceEquals(_copiedCts, cts)) CodeCopied = false;
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Asks the service at <paramref name="server"/> for a code, shows it and opens the service's page in the browser.</summary>
    public async Task StartAsync(string server)
    {
        if (!IsIdle || _disposed) return;
        ErrorText = null;
        CodeCopied = false;
        IsStarting = true;
        var starting = _starting = new CancellationTokenSource();
        try
        {
            var challenge = await _account.BeginSignInAsync(server.Trim(), starting.Token);
            if (_disposed || starting.IsCancellationRequested)
            {
                challenge.Cancel();
                return;
            }
            _challenge = challenge;
            ServerHost = challenge.VerificationUri.Host;
            UserCode = challenge.UserCode;
            _openBrowser(challenge.VerificationUri);
            _ = WaitAsync(challenge);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException e)
        {
            ErrorText = string.Format(MobileLangResources.Get(MobileLangResourceKind.SignInFailed), e.Message);
        }
        finally
        {
            IsStarting = false;
        }
    }

    /// <summary>Signs in directly at a self-hosted relay using its Access Key (no browser code).</summary>
    public async Task StartWithKeyAsync(string server, string accessKey)
    {
        if (!IsIdle || _disposed) return;
        ErrorText = null;
        CodeCopied = false;
        IsStarting = true;
        var starting = _starting = new CancellationTokenSource();
        try
        {
            await _account.SignInWithKeyAsync(server.Trim(), accessKey.Trim(), starting.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException e)
        {
            ErrorText = string.Format(MobileLangResources.Get(MobileLangResourceKind.SignInFailed), e.Message);
        }
        finally
        {
            IsStarting = false;
        }
    }

    /// <summary>Opens the service's page again (the browser was closed, or never came up).</summary>
    [RelayCommand]
    private void Browse()
    {
        if (_challenge is { } challenge) _openBrowser(challenge.VerificationUri);
    }

    /// <summary>Gives the code up: the page is back at where the sign-in was started.</summary>
    [RelayCommand]
    public void Cancel()
    {
        try { _starting?.Cancel(); }
        catch (ObjectDisposedException) { }
        _copiedCts?.Cancel();
        _challenge?.Cancel();
        _challenge = null;
        ServerHost = null;
        UserCode = null;
    }

    public void Dispose()
    {
        _disposed = true;
        try { _starting?.Cancel(); }
        catch (ObjectDisposedException) { }
        _copiedCts?.Cancel();
        _copiedCts?.Dispose();
        _challenge?.Cancel();
    }

    private async Task WaitAsync(SignInChallenge challenge)
    {
        SignInOutcome outcome;
        try { outcome = await challenge.Completion.ConfigureAwait(false); }
        catch (Exception e) when (e is OperationCanceledException or IOException) { outcome = SignInOutcome.Failed; }
        Dispatcher.UIThread.Post(() =>
        {
            // Signed in: the shell is already on its way to the host list. Given up here: nothing to say.
            if (_disposed || !ReferenceEquals(_challenge, challenge) || outcome is SignInOutcome.SignedIn or SignInOutcome.Cancelled) return;
            _challenge = null;
            UserCode = null;
            ErrorText = MobileLangResources.Get(outcome switch
            {
                SignInOutcome.Denied => MobileLangResourceKind.SignInDenied,
                SignInOutcome.Expired => MobileLangResourceKind.SignInExpired,
                _ => MobileLangResourceKind.SignInLost,
            });
        });
    }
}

/// <summary>
/// The first page of a phone that is neither signed in nor used without an account: "Sign in" (at the official
/// service, without asking for anything), and the other two ways to connect. The same page, with a way back and
/// without the local-network row, is what "Sign in" opens from the host list of a phone used without an account.
/// </summary>
public sealed partial class WelcomeViewModel : ObservableObject, IDisposable
{
    private readonly IAccountSession _account;
    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;

    /// <param name="isOnTop">On top of the host list rather than the first page.</param>
    public WelcomeViewModel(IAccountSession account, IRemoteWorkspace workspace, INavigator navigator, Action<Uri> openBrowser, bool isOnTop = false)
    {
        _account = account;
        _workspace = workspace;
        _navigator = navigator;
        IsOnTop = isOnTop;
        SignIn = new SignInFlow(account, openBrowser);
        SignIn.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(SignInFlow.IsIdle) or nameof(SignInFlow.HasCode))) return;
            IsCodeSheetOpen = SignIn.HasCode;
            OnPropertyChanged(nameof(IsChoosing));
            SignInCommand.NotifyCanExecuteChanged();
        };
    }

    public SignInFlow SignIn { get; }
    public bool IsOnTop { get; }
    /// <summary>Bottom sheet holding the 8-char verification code; dismissing it cancels sign-in.</summary>
    [ObservableProperty] public partial bool IsCodeSheetOpen { get; set; }
    /// <summary>This build knows the official service: "Sign in" goes there. Otherwise it asks for an address.</summary>
    public bool HasOfficialServer => _account.OfficialServer is not null;
    /// <summary>Short display host of the official service (e.g. "octoterm.si").</summary>
    public string? OfficialHost => FormatHost(_account.OfficialServer);
    public string SignInButtonText => OfficialHost is { Length: > 0 } host
        ? MobileLangResources.Format(MobileLangResourceKind.SignInWithOfficial, host)
        : MobileLangResources.Get(MobileLangResourceKind.SignInStart);
    public string RelayFeatureText => OfficialHost is { Length: > 0 } host
        ? MobileLangResources.Format(MobileLangResourceKind.WelcomeFeatureRelay, host)
        : MobileLangResources.Get(MobileLangResourceKind.WelcomeFeatureRelayGeneric);
    public bool ShowsHighlights => !IsOnTop;
    /// <summary>"Local network": not where the person already uses the app that way.</summary>
    public bool ShowsLocal => !IsOnTop;
    /// <summary>True when this platform provides a native QR code scanner.</summary>
    public bool CanScanQr => MobileHost.ScanQrCode is not null;
    /// <summary>Quick QR scan row on the initial welcome page when the device has a camera.</summary>
    public bool ShowsScanQr => ShowsLocal && CanScanQr;
    /// <summary>"Custom service" as a row of its own: only when "Sign in" does not lead there anyway.</summary>
    public bool ShowsCustom => HasOfficialServer;
    public bool ShowsOtherWays => ShowsScanQr || ShowsLocal || ShowsCustom;
    /// <summary>The buttons are showing rather than the code.</summary>
    public bool IsChoosing => !SignIn.HasCode;
    [ObservableProperty] public partial bool IsScanning { get; private set; }

    partial void OnIsCodeSheetOpenChanged(bool value)
    {
        if (!value && SignIn.HasCode) SignIn.Cancel();
    }

    internal static string? FormatHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        return Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : url.Trim();
    }

    private bool CanSignIn => SignIn.IsIdle;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        if (_account.OfficialServer is { } official) await SignIn.StartAsync(official);
        else _navigator.ShowCustomServer();
    }

    /// <summary>
    /// Without an account. A phone that is paired with a computer already goes straight to its list; one that is not
    /// adds its first computer, and only then counts as used that way.
    /// </summary>
    [RelayCommand]
    private void UseLocal()
    {
        SignIn.Cancel();
        if (_workspace.HasPairedHosts) _account.UseLocalOnly();
        else _navigator.ShowAddHost();
    }

    [RelayCommand]
    private async Task ScanQrAsync()
    {
        if (IsScanning || MobileHost.ScanQrCode is not { } scan) return;
        SignIn.Cancel();
        IsScanning = true;
        string? scanned;
        try { scanned = await scan(); }
        finally { IsScanning = false; }
        if (string.IsNullOrWhiteSpace(scanned)) return;
        var trimmed = scanned.Trim();
        if (RelayConfigLink.TryParse(trimmed) is not null) _navigator.ShowCustomServer(trimmed);
        else _navigator.ShowAddHost(trimmed, autoConnect: true);
    }

    [RelayCommand]
    private void UseCustom()
    {
        SignIn.Cancel();
        _navigator.ShowCustomServer();
    }

    [RelayCommand]
    private void Back()
    {
        SignIn.Cancel();
        _navigator.Back();
    }

    public void Dispose() => SignIn.Dispose();
}

/// <summary>Signing in at a service of the person's own: its address and "Sign in", then the code to confirm in the browser.</summary>
public sealed partial class CustomServerViewModel : ObservableObject, IDisposable
{
    private readonly INavigator _navigator;

    public CustomServerViewModel(IAccountSession account, INavigator navigator, Action<Uri> openBrowser, string? initialLink = null)
    {
        _navigator = navigator;
        Server = account.Server;
        SignIn = new SignInFlow(account, openBrowser);
        SignIn.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(SignInFlow.IsIdle) or nameof(SignInFlow.HasCode))) return;
            OnPropertyChanged(nameof(IsEntering));
            Notify();
        };
        if (!string.IsNullOrWhiteSpace(initialLink))
        {
            Server = initialLink.Trim();
        }
    }

    public SignInFlow SignIn { get; }
    /// <summary>The address as typed; the last one used is there to start with. Also accepts <c>octopus://relay?…</c>.</summary>
    [ObservableProperty] public partial string Server { get; set; }
    /// <summary>Optional Access Key for browser-free sign-in on a self-hosted Octopus Relay.</summary>
    [ObservableProperty] public partial string AccessKey { get; set; } = "";
    [ObservableProperty] public partial bool IsScanning { get; private set; }

    /// <summary>The address is showing rather than the code.</summary>
    public bool IsEntering => !SignIn.HasCode;
    public bool CanScanQr => MobileHost.ScanQrCode is not null;
    public bool CanStart => SignIn.IsIdle && !string.IsNullOrWhiteSpace(Server);

    partial void OnServerChanged(string value)
    {
        if (RelayConfigLink.TryParse(value) is { } link)
        {
            Server = link.Url.GetLeftPart(UriPartial.Authority);
            if (!string.IsNullOrEmpty(link.AccessKey))
            {
                AccessKey = link.AccessKey;
            }
            return;
        }

        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync()
    {
        var server = Server.Trim();
        var key = AccessKey.Trim();
        if (RelayConfigLink.TryParse(server) is { } link)
        {
            server = link.Url.GetLeftPart(UriPartial.Authority);
            if (!string.IsNullOrEmpty(link.AccessKey)) key = link.AccessKey;
        }

        return string.IsNullOrEmpty(key) ? SignIn.StartAsync(server) : SignIn.StartWithKeyAsync(server, key);
    }

    [RelayCommand]
    private async Task ScanQrAsync()
    {
        if (IsScanning || MobileHost.ScanQrCode is not { } scan) return;
        IsScanning = true;
        string? scanned;
        try { scanned = await scan(); }
        finally { IsScanning = false; }
        if (string.IsNullOrWhiteSpace(scanned)) return;
        var trimmed = scanned.Trim();
        if (PairingLink.TryParse(trimmed) is not null)
        {
            _navigator.ShowAddHost(trimmed, autoConnect: true);
            return;
        }
        Server = trimmed;
    }

    [RelayCommand]
    private void Back()
    {
        SignIn.Cancel();
        _navigator.Back();
    }

    public void Dispose() => SignIn.Dispose();
}

/// <summary>
/// Adding a computer without a service's help: its address on the local network, or a pairing link copied from it
/// (~/code/Octopus/design/mobile/01-登录与连接方式.md §4). With an address the computer shows a number, which the
/// pairing page shows too; with a link there is nothing to compare.
/// </summary>
public sealed partial class AddHostViewModel : ObservableObject, IDisposable
{
    private readonly IRemoteWorkspace _workspace;
    private readonly IAccountSession _account;
    private readonly INavigator _navigator;
    private PairingAttempt? _attempt;
    private bool _disposed;

    public AddHostViewModel(IRemoteWorkspace workspace, IAccountSession account, INavigator navigator, string? initialTarget = null, bool autoConnect = false)
    {
        _workspace = workspace;
        _account = account;
        _navigator = navigator;
        if (!string.IsNullOrWhiteSpace(initialTarget))
        {
            Target = initialTarget.Trim();
            if (autoConnect)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && ConnectCommand.CanExecute(null)) ConnectCommand.Execute(null);
                });
            }
        }
    }

    /// <summary>What the person typed or pasted: an address or a pairing link.</summary>
    [ObservableProperty] public partial string Target { get; set; } = "";
    /// <summary>The computer is being reached, or the person there is being waited for.</summary>
    [ObservableProperty] public partial bool IsConnecting { get; private set; }
    [ObservableProperty] public partial bool IsScanning { get; private set; }
    [ObservableProperty] public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public bool CanScanQr => MobileHost.ScanQrCode is not null;
    public bool CanStartScan => CanScanQr && !IsConnecting && !IsScanning;
    public bool CanConnect => !IsConnecting && !IsScanning && !string.IsNullOrWhiteSpace(Target);

    partial void OnTargetChanged(string value) => Notify();
    partial void OnIsConnectingChanged(bool value) => Notify();
    partial void OnIsScanningChanged(bool value) => Notify();
    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    private void Notify()
    {
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanStartScan));
        ConnectCommand.NotifyCanExecuteChanged();
        ScanQrCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStartScan))]
    private async Task ScanQrAsync()
    {
        if (MobileHost.ScanQrCode is not { } scan) return;
        ErrorText = null;
        IsScanning = true;
        string? scanned;
        try { scanned = await scan(); }
        finally { IsScanning = false; }
        if (_disposed || string.IsNullOrWhiteSpace(scanned)) return;
        var trimmed = scanned.Trim();
        if (RelayConfigLink.TryParse(trimmed) is not null)
        {
            _navigator.ShowCustomServer(trimmed);
            return;
        }
        Target = trimmed;
        await ConnectAsync();
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        var target = Target.Trim();
        ErrorText = null;
        IsConnecting = true;
        var attempt = _attempt = _workspace.AddHost(target);
        _ = ShowNumberAsync(attempt, target);
        PairingOutcome outcome;
        try { outcome = await attempt.Completion; }
        catch (OperationCanceledException) { outcome = PairingOutcome.Cancelled; }
        catch (IOException) { outcome = PairingOutcome.Unreachable; }
        // Left, or tried again meanwhile: this answer is nobody's any more.
        if (_disposed || !ReferenceEquals(_attempt, attempt)) return;
        _attempt = null;
        IsConnecting = false;
        switch (outcome)
        {
            case PairingOutcome.Paired:
                // The first computer of a phone that had chosen nothing yet: from now on it is used without an account,
                // and the shell shows its list. Otherwise the list is already under this page.
                if (!_account.IsSignedIn && !_account.IsLocalOnly) _account.UseLocalOnly();
                else _navigator.ShowFirst();
                break;
            case PairingOutcome.Cancelled:
                break;
            default:
                var baseError = MobileLangResources.Get(outcome switch
                {
                    PairingOutcome.NotAnAddress => MobileLangResourceKind.AddHostNotAnAddress,
                    PairingOutcome.PairingLinkNoAddress => MobileLangResourceKind.AddHostPairingLinkNoAddress,
                    PairingOutcome.NotPairing => MobileLangResourceKind.PairingNotOpen,
                    PairingOutcome.Unreachable => MobileLangResourceKind.PairingUnreachable,
                    _ => MobileLangResourceKind.PairingFailed,
                });
                var detail = outcome == PairingOutcome.Unreachable ? attempt.ErrorDetail?.Invoke() : null;
                ErrorText = string.IsNullOrWhiteSpace(detail) ? baseError : $"{baseError} ({detail})";
                break;
        }
    }

    [RelayCommand]
    private void Back()
    {
        Abandon();
        _navigator.Back();
    }

    public void Dispose()
    {
        _disposed = true;
        Abandon();
    }

    private void Abandon()
    {
        var attempt = _attempt;
        _attempt = null;
        IsConnecting = false;
        attempt?.Cancel();
    }

    // The computer answered with its number: from here on the page is the one every pairing has.
    private async Task ShowNumberAsync(PairingAttempt attempt, string target)
    {
        try { await attempt.Code; }
        catch (Exception e) when (e is OperationCanceledException or IOException) { return; }
        if (_disposed || !ReferenceEquals(_attempt, attempt)) return;
        _navigator.ShowPairing(target, attempt);
    }
}

/// <summary>Pairing with one host: the number both sides show, until the host allows this phone or refuses.</summary>
public sealed partial class PairingViewModel : ObservableObject, IDisposable
{
    private readonly INavigator _navigator;
    private readonly PairingAttempt _attempt;
    // The page that started the pairing decides where to go once it worked; this one only leaves when it failed or was given up.
    private readonly bool _isShared;
    private bool _left;

    /// <summary>Pairs with a computer of the account's list.</summary>
    public PairingViewModel(RemoteHost host, IRemoteWorkspace workspace, INavigator navigator)
        : this(host.Name, workspace.BeginPairing(host), navigator, isShared: false)
    {
    }

    /// <summary>Shows a pairing that is already under way (a computer added by its address), from the number on.</summary>
    public PairingViewModel(string hostName, PairingAttempt attempt, INavigator navigator)
        : this(hostName, attempt, navigator, isShared: true)
    {
    }

    private PairingViewModel(string hostName, PairingAttempt attempt, INavigator navigator, bool isShared)
    {
        _navigator = navigator;
        _isShared = isShared;
        HostName = hostName;
        _attempt = attempt;
        _ = ShowCodeAsync();
        _ = WaitAsync();
    }

    public string HostName { get; }
    /// <summary>Where "back" leads: the page the computer was added on, or the list.</summary>
    public string BackText => MobileLangResources.Get(_isShared ? MobileLangResourceKind.Back : MobileLangResourceKind.Hosts);
    /// <summary>The number to compare with the one on the computer; null until the computer answered.</summary>
    [ObservableProperty] public partial string? Code { get; private set; }
    [ObservableProperty] public partial bool IsWaiting { get; private set; } = true;
    [ObservableProperty] public partial string? FailureText { get; private set; }

    public bool HasCode => Code is not null;
    /// <summary>Before the number: the computer is being reached.</summary>
    public bool IsContacting => IsWaiting && !HasCode;
    /// <summary>The number is shown and the person at the computer has to allow.</summary>
    public bool IsComparing => IsWaiting && HasCode;
    public bool HasFailed => FailureText is not null;

    partial void OnCodeChanged(string? value) => NotifyStage();
    partial void OnIsWaitingChanged(bool value) => NotifyStage();
    partial void OnFailureTextChanged(string? value) => OnPropertyChanged(nameof(HasFailed));

    private void NotifyStage()
    {
        OnPropertyChanged(nameof(HasCode));
        OnPropertyChanged(nameof(IsContacting));
        OnPropertyChanged(nameof(IsComparing));
    }

    [RelayCommand]
    private void Cancel()
    {
        _attempt.Cancel();
        Leave();
    }

    public void Dispose() => _attempt.Cancel();

    private async Task ShowCodeAsync()
    {
        string code;
        try { code = await _attempt.Code.ConfigureAwait(false); }
        catch (Exception e) when (e is OperationCanceledException or IOException) { return; }
        // Three digits, a gap, three digits: easier to compare with the number on the computer.
        var shown = code.Length == 6 ? code[..3] + " " + code[3..] : code;
        Dispatcher.UIThread.Post(() => Code = shown);
    }

    private async Task WaitAsync()
    {
        PairingOutcome outcome;
        try { outcome = await _attempt.Completion.ConfigureAwait(false); }
        catch (OperationCanceledException) { outcome = PairingOutcome.Cancelled; }
        catch (IOException) { outcome = PairingOutcome.Unreachable; }
        Dispatcher.UIThread.Post(() =>
        {
            IsWaiting = false;
            if (outcome == PairingOutcome.Paired)
            {
                if (!_isShared) Leave();
            }
            else if (!_left && outcome != PairingOutcome.Cancelled)
            {
                FailureText = MobileLangResources.Get(outcome switch
                {
                    PairingOutcome.NotPairing => MobileLangResourceKind.PairingNotOpen,
                    PairingOutcome.Unreachable => MobileLangResourceKind.PairingUnreachable,
                    _ => MobileLangResourceKind.PairingFailed,
                });
            }
        });
    }

    // Once: the host's answer and the Cancel button can both arrive.
    private void Leave()
    {
        if (_left) return;
        _left = true;
        _navigator.Back();
    }
}
