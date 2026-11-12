using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Common;

namespace Octopus.App.Settings.Agents;

public enum TextFieldKind
{
    SingleLine,
    MultiLine,
    File,
    Directory
}

/// <summary>Free text: <c>string</c>, <c>text</c>, <c>file</c> and <c>directory</c> features. Writes after typing pauses.</summary>
public sealed partial class TextFieldViewModel : AgentFieldViewModel
{
    private readonly Action<string?> _write;
    private readonly Debouncer _debounce = new();

    [ObservableProperty]
    private string _text;

    public TextFieldViewModel(
        AgentRowViewModel row,
        string key,
        string label,
        TextFieldKind kind,
        string value,
        Action<string?> write,
        string? placeholder = null,
        string? description = null)
        : base(row, key, label, description)
    {
        Kind = kind;
        _text = value;
        _write = write;
        Placeholder = placeholder;
    }

    public TextFieldKind Kind { get; }

    public string? Placeholder { get; }

    public bool IsMultiLine => Kind == TextFieldKind.MultiLine;

    public bool CanBrowse => Kind is TextFieldKind.File or TextFieldKind.Directory;

    partial void OnTextChanged(string value) =>
        _debounce.Schedule(() => _write(string.IsNullOrWhiteSpace(value) ? null : value));

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var picked = await Row.PathPicker.PickAsync(Kind == TextFieldKind.Directory, Label);
        if (picked is not null)
        {
            Text = picked;
            Flush();
        }
    }

    public override void Flush() => _debounce.Flush();
}
