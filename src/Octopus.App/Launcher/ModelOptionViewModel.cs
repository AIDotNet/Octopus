using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Octopus.App.Launcher;

/// <summary>One row of the model list in the agent panel.</summary>
public sealed partial class ModelOptionViewModel(string? id, string label, string? note = null, bool isCustom = false, Action<ModelOptionViewModel>? select = null) : ObservableObject
{
    /// <summary>The model id passed to the agent; null is "the agent's default".</summary>
    public string? Id { get; } = id;
    public string Label { get; } = label;
    public string? Note { get; } = note;
    public bool IsCustom { get; } = isCustom;

    [RelayCommand]
    private void Select() => select?.Invoke(this);

    [ObservableProperty] public partial bool IsSelected { get; set; }
}
