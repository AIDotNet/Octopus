using CommunityToolkit.Mvvm.ComponentModel;

namespace Octopus.App.Settings.Agents;

/// <summary>An on/off switch: fast mode and <c>bool</c> features.</summary>
public sealed partial class ToggleFieldViewModel : AgentFieldViewModel
{
    private readonly Action<bool> _write;

    [ObservableProperty]
    private bool _isOn;

    public ToggleFieldViewModel(AgentRowViewModel row, string key, string label, bool value, Action<bool> write, string? description = null)
        : base(row, key, label, description)
    {
        _isOn = value;
        _write = write;
    }

    partial void OnIsOnChanged(bool value) => _write(value);
}
