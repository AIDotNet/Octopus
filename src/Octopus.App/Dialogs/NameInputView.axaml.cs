using Avalonia.Controls;
using Avalonia.Interactivity;
using TKCode.Arc;

namespace Octopus.App.Dialogs;

/// <summary>A single name in a dialog: a new file, a new folder or a rename.</summary>
public partial class NameInputView : UserControl
{
    public NameInputView() : this("", null)
    {
    }

    public NameInputView(string label, string? initial)
    {
        InitializeComponent();
        // The label is set here, not in markup, so that nothing rebinds it to another text later.
        NameInput.Label = label;
        NameInput.Text = initial ?? "";
    }

    /// <summary>What was typed, trimmed.</summary>
    public string Name => (NameInput.Text ?? "").Trim();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        NameInput.Focus();
    }
}
