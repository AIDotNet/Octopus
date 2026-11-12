using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Appearance;
using Octopus.App.Common;
using TKCode.Arc;
using TKCode.Arc.Localization;

namespace Octopus.App.Settings.Appearance;

/// <summary>
/// The appearance page: theme and accent. It only drives Arc's theme (<see cref="ArcTheme.Apply"/>); ThemeService saves
/// the result, and this page follows changes made elsewhere (the switch in the top bar).
/// </summary>
public sealed partial class AppearanceSettingsViewModel : ObservableObject, IDisposable
{
    [ObservableProperty]
    private string _theme;

    public AppearanceSettingsViewModel()
    {
        _theme = ThemeService.Format(ArcTheme.State.Appearance);
        Accents = [.. Enum.GetValues<ArcAccentPreset>().Select(p => new AccentChoiceViewModel(p))];
        UpdateAccentSelection();
        ArcTheme.Manager.StateChanged += OnArcThemeChanged;
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
        ArcLanguage.Changed -= OnLanguageChanged;
    }
}
