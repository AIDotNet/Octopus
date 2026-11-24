using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;
using Octopus.Remote.Security;

namespace Octopus.Mobile.Account;

/// <summary>
/// Getting into a computer of the account with its access password (design/16-账号设备与访问密码.md): typed once,
/// then the computer is remembered like a paired one. The field shows what is typed as <c>ABCD-EFGH</c>, upper case,
/// whatever was pasted; the computer's answer takes a moment (both sides derive a key from the password).
/// </summary>
public sealed partial class AccessPasswordViewModel : ObservableObject, IDisposable
{
    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;
    private CancellationTokenSource? _verifying;
    private bool _left;

    public AccessPasswordViewModel(RemoteHost host, IRemoteWorkspace workspace, INavigator navigator)
    {
        Host = host;
        _workspace = workspace;
        _navigator = navigator;
    }

    public RemoteHost Host { get; }
    /// <summary>The page's title: the computer's name.</summary>
    public string HostName => Host.Name;

    /// <summary>What is typed, as shown: letters and digits only, upper case, at most eight, a dash after the fourth.</summary>
    [ObservableProperty] public partial string Password { get; set; } = "";
    /// <summary>The computer is being asked (the key derivation takes a moment).</summary>
    [ObservableProperty] public partial bool IsVerifying { get; private set; }
    [ObservableProperty] public partial string? ErrorText { get; private set; }
    /// <summary>The computer does not take its password now: pairing with the six digits is offered instead.</summary>
    [ObservableProperty] public partial bool CanPairInstead { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    /// <summary>Eight letters and digits are there, and nothing is under way.</summary>
    public bool CanVerify => !IsVerifying && AccessPassword.Normalize(Password) is not null;
    /// <summary>What the primary button says: "Connect", or "Verifying…" while the computer is asked.</summary>
    public string VerifyText => MobileLangResources.Get(IsVerifying ? MobileLangResourceKind.AccessPasswordVerifying : MobileLangResourceKind.AccessPasswordSubmit);

    /// <summary>
    /// The form the field shows for whatever was typed or pasted: spaces, dashes and anything that is not a letter or
    /// digit dropped, letters upper-cased, cut at eight, and a dash after the fourth (<c>ABCD-EFGH</c>).
    /// </summary>
    public static string FormatTyped(string? typed)
    {
        var kept = new string([.. (typed ?? "").Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).Take(AccessPassword.Length)]);
        return kept.Length > 4 ? kept[..4] + "-" + kept[4..] : kept;
    }

    partial void OnPasswordChanged(string value)
    {
        var formatted = FormatTyped(value);
        if (formatted != value)
        {
            // Raises this again with the formatted text, which is where the rest happens.
            Password = formatted;
            return;
        }
        if (!IsVerifying)
        {
            // Typing again: the last answer was about another password.
            ErrorText = null;
            CanPairInstead = false;
        }
        Notify();
    }

    partial void OnIsVerifyingChanged(bool value)
    {
        OnPropertyChanged(nameof(VerifyText));
        Notify();
    }

    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    private void Notify()
    {
        OnPropertyChanged(nameof(CanVerify));
        VerifyCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private async Task VerifyAsync()
    {
        var password = Password;
        ErrorText = null;
        CanPairInstead = false;
        IsVerifying = true;
        var verifying = _verifying = new CancellationTokenSource();
        AccessOutcome outcome;
        try { outcome = await _workspace.AccessWithPasswordAsync(Host, password, verifying.Token); }
        catch (OperationCanceledException) { outcome = AccessOutcome.Cancelled; }
        catch (IOException) { outcome = AccessOutcome.Unreachable; }
        // Left meanwhile: this answer is nobody's any more.
        if (_left || !ReferenceEquals(_verifying, verifying)) return;
        _verifying = null;
        verifying.Dispose();
        IsVerifying = false;
        switch (outcome)
        {
            case AccessOutcome.Granted:
                // In: the computer's page takes this one's place, as if it had been opened from the list.
                _left = true;
                _navigator.Back();
                _navigator.ShowHost(Host);
                return;
            case AccessOutcome.Cancelled:
                return;
            case AccessOutcome.PasswordOff:
                CanPairInstead = true;
                break;
        }
        ErrorText = MobileLangResources.Get(outcome switch
        {
            AccessOutcome.WrongPassword => MobileLangResourceKind.AccessPasswordWrong,
            AccessOutcome.Locked => MobileLangResourceKind.AccessPasswordLocked,
            AccessOutcome.PasswordOff => MobileLangResourceKind.AccessPasswordOff,
            AccessOutcome.NotConfirmed => MobileLangResourceKind.AccessPasswordNotConfirmed,
            AccessOutcome.NotAPassword => MobileLangResourceKind.AccessPasswordInvalid,
            _ => MobileLangResourceKind.AccessPasswordUnreachable,
        });
    }

    /// <summary>The computer does not take its password: the six digits instead, from its row as before.</summary>
    [RelayCommand]
    private void PairInstead()
    {
        if (_left) return;
        _left = true;
        Abandon();
        _navigator.Back();
        _navigator.ShowPairing(Host);
    }

    [RelayCommand]
    private void Back()
    {
        if (_left) return;
        _left = true;
        Abandon();
        _navigator.Back();
    }

    public void Dispose()
    {
        _left = true;
        Abandon();
    }

    private void Abandon()
    {
        var verifying = _verifying;
        _verifying = null;
        try { verifying?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
