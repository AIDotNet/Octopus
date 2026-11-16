using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Account;

/// <summary>
/// The page shown while signed out: the service's address and "Sign in", then a device code to confirm in the browser.
/// The shell replaces this page with the host list as soon as the account is signed in.
/// </summary>
public sealed partial class SignInViewModel : ObservableObject, IDisposable
{
    private readonly IAccountSession _account;
    private readonly Action<Uri> _openBrowser;
    private SignInChallenge? _challenge;
    private CancellationTokenSource? _starting;
    private bool _disposed;

    public SignInViewModel(IAccountSession account, Action<Uri> openBrowser)
    {
        _account = account;
        _openBrowser = openBrowser;
        Server = account.Server;
        // Signed out by the service, not by the person: said once, where they sign in again.
        ErrorText = account.SignInEnded ? MobileLangResources.Get(MobileLangResourceKind.SignInEnded) : null;
    }

    [ObservableProperty] public partial string Server { get; set; }
    /// <summary>The code to confirm in the browser; null until the service gave one.</summary>
    [ObservableProperty] public partial string? UserCode { get; private set; }
    /// <summary>The service is being asked for a code.</summary>
    [ObservableProperty] public partial bool IsStarting { get; private set; }
    [ObservableProperty] public partial string? ErrorText { get; private set; }

    public bool HasCode => UserCode is not null;
    public bool IsEntering => !HasCode;
    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    public bool CanStart => !IsStarting && !HasCode && !string.IsNullOrWhiteSpace(Server);

    partial void OnUserCodeChanged(string? value)
    {
        OnPropertyChanged(nameof(HasCode));
        OnPropertyChanged(nameof(IsEntering));
        Notify();
    }

    partial void OnIsStartingChanged(bool value) => Notify();
    partial void OnServerChanged(string value) => Notify();
    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    private void Notify()
    {
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Asks the service for a code, shows it and opens the service's page in the browser.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        ErrorText = null;
        IsStarting = true;
        var starting = _starting = new CancellationTokenSource();
        try
        {
            var challenge = await _account.BeginSignInAsync(Server.Trim(), starting.Token);
            if (_disposed || starting.IsCancellationRequested)
            {
                challenge.Cancel();
                return;
            }
            _challenge = challenge;
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

    /// <summary>Opens the service's page again (the browser was closed, or never came up).</summary>
    [RelayCommand]
    private void Browse()
    {
        if (_challenge is { } challenge) _openBrowser(challenge.VerificationUri);
    }

    /// <summary>Gives the code up and goes back to the address.</summary>
    [RelayCommand]
    private void Cancel()
    {
        _challenge?.Cancel();
        _challenge = null;
        UserCode = null;
    }

    public void Dispose()
    {
        _disposed = true;
        try { _starting?.Cancel(); }
        catch (ObjectDisposedException) { }
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

/// <summary>Pairing with one host: the number both sides show, until the host allows this phone or refuses.</summary>
public sealed partial class PairingViewModel : ObservableObject, IDisposable
{
    private readonly INavigator _navigator;
    private readonly PairingAttempt _attempt;
    private bool _left;

    public PairingViewModel(RemoteHost host, IRemoteWorkspace workspace, INavigator navigator)
    {
        _navigator = navigator;
        HostName = host.Name;
        _attempt = workspace.BeginPairing(host);
        _ = ShowCodeAsync();
        _ = WaitAsync();
    }

    public string HostName { get; }
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
            if (outcome == PairingOutcome.Paired) Leave();
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
