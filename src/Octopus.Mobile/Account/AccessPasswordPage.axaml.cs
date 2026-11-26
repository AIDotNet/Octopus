using System.ComponentModel;
using Avalonia.Controls;
using Octopus.Mobile.Shell;
using Page = TKCode.Arc.Mobile.Page;
using TextField = TKCode.Arc.Mobile.TextField;

namespace Octopus.Mobile.Account;

/// <summary>
/// A computer's access password, typed once (design/16-账号设备与访问密码.md). The field and the view model's
/// <see cref="AccessPasswordViewModel.Password"/> are kept in step here rather than by a binding: the text is
/// re-formatted as it is typed (<c>ABCD-EFGH</c>), and the caret has to stay after what was just typed rather than
/// land before the dash that was put in.
/// </summary>
public partial class AccessPasswordPage : Page, IClosablePage
{
    private readonly AccessPasswordViewModel _viewModel;
    private bool _syncing;

    public AccessPasswordPage(AccessPasswordViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        PasswordInput.Text = viewModel.Password;
        PasswordInput.TextChanged += OnTextChanged;
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    /// <summary>The field the password is typed into.</summary>
    internal TextField Field => PasswordInput;

    public void OnClosed()
    {
        PasswordInput.TextChanged -= OnTextChanged;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.Dispose();
    }

    // Typed or pasted: the view model formats it, and the field shows that with the caret after the same character.
    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        var typed = PasswordInput.Text ?? "";
        var caret = Math.Clamp(PasswordInput.CaretIndex, 0, typed.Length);
        var kept = typed[..caret].Count(char.IsAsciiLetterOrDigit);
        _syncing = true;
        try
        {
            _viewModel.Password = typed;
            var shown = _viewModel.Password;
            if (shown != typed)
            {
                PasswordInput.Text = shown;
                PasswordInput.CaretIndex = CaretAfter(shown, kept);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    // Set by the view model itself (a test, or a reset): shown with the caret at the end.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || e.PropertyName != nameof(AccessPasswordViewModel.Password) || PasswordInput.Text == _viewModel.Password) return;
        _syncing = true;
        try
        {
            PasswordInput.Text = _viewModel.Password;
            PasswordInput.CaretIndex = _viewModel.Password.Length;
        }
        finally
        {
            _syncing = false;
        }
    }

    // Where the caret goes in the formatted text: right after its <paramref name="kept"/>th letter or digit.
    private static int CaretAfter(string shown, int kept)
    {
        if (kept <= 0) return 0;
        var seen = 0;
        for (var i = 0; i < shown.Length; i++)
        {
            if (char.IsAsciiLetterOrDigit(shown[i]) && ++seen == kept) return i + 1;
        }
        return shown.Length;
    }
}
