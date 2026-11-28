using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Octopus.App.Launcher;

/// <summary>One row of the model list in the agent panel.</summary>
public sealed partial class ModelOptionViewModel(
    string? id,
    string label,
    string? note = null,
    bool isCustom = false,
    Action<ModelOptionViewModel>? select = null,
    string? iconKey = null,
    string? switchBackendRef = null,
    string? sourceBadge = null) : ObservableObject
{
    /// <summary>The model id passed to the agent; null is "the agent's default".</summary>
    public string? Id { get; } = id;
    public string Label { get; } = label;
    public string? Note { get; } = note;
    public bool IsCustom { get; } = isCustom;
    public string? IconKey { get; } = iconKey;

    /// <summary>When non-null, clicking this row also switches the active provider/source to this backend ref.</summary>
    public string? SwitchBackendRef { get; } = switchBackendRef;

    /// <summary>Optional compact source tag shown on cross-provider search matches.</summary>
    public string? SourceBadge { get; } = sourceBadge;
    public bool HasSourceBadge => !string.IsNullOrEmpty(SourceBadge);

    [RelayCommand]
    private void Select() => select?.Invoke(this);

    [ObservableProperty] public partial bool IsSelected { get; set; }
}
