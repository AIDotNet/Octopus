using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Octopus.App.Settings.Agents;

/// <summary>One <c>NAME=value</c> line of the environment list.</summary>
public sealed partial class EnvEntryViewModel : ObservableObject
{
    private readonly EnvFieldViewModel _owner;

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _value;

    [ObservableProperty]
    private bool _isSecret;

    public EnvEntryViewModel(EnvFieldViewModel owner, string name, string value)
    {
        _owner = owner;
        _name = name;
        _value = value;
        _isSecret = owner.IsSecret(name);
    }

    /// <summary>What the variable is, when the definition knows it (shown as the tooltip of the name).</summary>
    public string? Hint => _owner.HintOf(Name);

    partial void OnNameChanged(string value)
    {
        IsSecret = _owner.IsSecret(value);
        OnPropertyChanged(nameof(Hint));
        _owner.EntryChanged();
    }

    partial void OnValueChanged(string value) => _owner.EntryChanged();

    [RelayCommand]
    private void Remove() => _owner.Remove(this);
}
