using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Appearance;
using Octopus.App.Common;
using Octopus.App.Composition;
using TKCode.Arc;
using TKCode.Arc.Localization;

namespace Octopus.App.Settings.Appearance;

/// <summary>
/// The appearance page: theme, accent, and interface layout style (compact vs. comfortable).
/// Theme and accent drive Arc's theme (<see cref="ArcTheme.Apply"/>); layout style drives <see cref="ThemeService.SetDensity"/>.
/// </summary>
public sealed partial class AppearanceSettingsViewModel : ObservableObject, IDisposable
{
    private readonly ThemeService _themeService;

    [ObservableProperty]
    private string _theme;

    [ObservableProperty]
    private string _density;

    public AppearanceSettingsViewModel(ThemeService? themeService = null)
    {
        _themeService = themeService ?? AppHost.Get<ThemeService>();
        _theme = ThemeService.Format(ArcTheme.State.Appearance);
        _density = _themeService.Density;
        Accents = [.. Enum.GetValues<ArcAccentPreset>().Select(p => new AccentChoiceViewModel(p))];
        UpdateAccentSelection();
        ArcTheme.Manager.StateChanged += OnArcThemeChanged;
        _themeService.DensityChanged += OnDensityServiceChanged;
        ArcLanguage.Changed += OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, LanguageChangedEventArgs e) =>
        UiContext.Run(() =>
        {
            foreach (var accent in Accents)
            {
                accent.RefreshName();
            }
        });

    public IReadOnlyList<AccentChoiceViewModel> Accents { get; }

    partial void OnThemeChanged(string value)
    {
        var appearance = ThemeService.Parse(value);
        if (appearance != ArcTheme.State.Appearance)
        {
            ArcTheme.Apply(t => t.Appearance(appearance));
        }
    }

    partial void OnDensityChanged(string value)
    {
        var normalized = UiDensityMetrics.Normalize(value);
        if (normalized != value)
        {
            Density = normalized;
            return;
        }
        if (normalized != _themeService.Density)
        {
            _themeService.SetDensity(normalized);
        }
    }

    [RelayCommand]
    private void SelectAccent(AccentChoiceViewModel? choice)
    {
        if (choice is not null)
        {
            ArcTheme.Apply(t => t.Accent(choice.Preset));
        }
    }

    private void OnArcThemeChanged(object? sender, ArcThemeChangedEventArgs e)
    {
        Theme = ThemeService.Format(e.NewState.Appearance);
        UpdateAccentSelection();
    }

    private void OnDensityServiceChanged(object? sender, UiDensityMetrics metrics)
    {
        Density = metrics.Key;
    }

    private void UpdateAccentSelection()
    {
        var name = ThemeChoices.AccentName(ArcTheme.State.Accent);
        foreach (var accent in Accents)
        {
            accent.IsSelected = accent.Preset.ToString() == name;
        }
    }

    public void Dispose()
    {
        ArcTheme.Manager.StateChanged -= OnArcThemeChanged;
        _themeService.DensityChanged -= OnDensityServiceChanged;
        ArcLanguage.Changed -= OnLanguageChanged;
    }
}
