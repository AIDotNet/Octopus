using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Octopus.App.Localization;
using TKCode.Arc;

namespace Octopus.App.Settings.Appearance;

/// <summary>One of Arc's accent presets, drawn as a colored dot.</summary>
public sealed partial class AccentChoiceViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public AccentChoiceViewModel(ArcAccentPreset preset)
    {
        Preset = preset;
        Brush = new SolidColorBrush(ArcPalette.GetSeed(preset));
    }

    public ArcAccentPreset Preset { get; }

    public IBrush Brush { get; }

    public string Name => Enum.TryParse<AppLangResourceKind>("Accent" + Preset, out var kind) ? AppLangResources.Get(kind) : Preset.ToString();

    /// <summary>The language changed.</summary>
    public void RefreshName() => OnPropertyChanged(nameof(Name));
}
