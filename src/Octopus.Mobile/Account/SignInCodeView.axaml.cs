using Avalonia.Controls;

namespace Octopus.Mobile.Account;

/// <summary>The second step of every sign-in: the code, "Open browser" and the wait. Its data context is a <see cref="SignInFlow"/>.</summary>
public partial class SignInCodeView : UserControl
{
    public SignInCodeView() => InitializeComponent();
}
