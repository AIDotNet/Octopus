using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Octopus.App.Launcher;

/// <summary>One installed agent in the first level of the launcher.</summary>
public sealed partial class LauncherAgentViewModel(
    string id,
    string name,
    bool isDefault,
    Action<LauncherAgentViewModel> launch,
    Action<LauncherAgentViewModel> expand) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public bool IsDefault { get; } = isDefault;

    [ObservableProperty] public partial bool IsExpanded { get; set; }

    /// <summary>Starts the agent with its default configuration.</summary>
    [RelayCommand]
    private void Launch() => launch(this);

    /// <summary>Shows the second panel (the arrow at the end of the row).</summary>
    [RelayCommand]
    private void Expand() => expand(this);
}
