using CommunityToolkit.Mvvm.ComponentModel;

namespace Octopus.App.Settings.Agents;

/// <summary>A list of short values, one tag each: extra arguments and <c>list</c> features.</summary>
public sealed partial class ArgsFieldViewModel : AgentFieldViewModel
{
    private readonly Action<IReadOnlyList<string>> _write;

    [ObservableProperty]
    private IReadOnlyList<string> _value;

    public ArgsFieldViewModel(
        AgentRowViewModel row,
        string key,
        string label,
        IReadOnlyList<string> value,
        Action<IReadOnlyList<string>> write,
        string placeholder,
        string? description = null)
        : base(row, key, label, description)
    {
        _value = value;
        _write = write;
        Placeholder = placeholder;
    }

    public string Placeholder { get; }

    partial void OnValueChanged(IReadOnlyList<string> value) => _write(value ?? []);
}
