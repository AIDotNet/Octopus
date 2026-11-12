using Avalonia.Media;
using Octopus.Storage;
using TKCode.Arc;
using TKCode.Arc.Themes.Corners;

namespace Octopus.App.Appearance;

/// <summary>Keeps Arc's theme and settings.json in step: applies the saved theme at start-up and saves every change.</summary>
public sealed class ThemeService(ISettingsStore settings) : IDisposable
{
    private EventHandler<ArcThemeChangedEventArgs>? _tracker;

    /// <summary>The theme of the first frame (passed to <c>WithTheme</c>).</summary>
    public void ApplyStartupTheme(ArcThemeBuilder theme)
    {
        var appearance = settings.Current.Appearance;
        ConfigureWorkspace(theme);
        theme.Appearance(Parse(appearance.Theme));
        if (appearance.Accent is not null && Color.TryParse(appearance.Accent, out var seed))
        {
            theme.Accent(seed);
        }
        else
        {
            theme.Accent(ArcAccentPreset.Neutral);
        }
    }

    /// <summary>
    /// Compact corners, and a dock layout that starts at the window's top edge: no padding around it, a thin gap between
    /// groups, and tab strips as tall as the top row (design 03 section 1.1).
    /// </summary>
    public static void ConfigureWorkspace(ArcThemeBuilder theme) =>
        theme.Corners(ArcCornerStyle.Scale(0.4))
             .Token("DockLayout", "Padding", "0")
             .Token("DockLayout", "Gap", "4")
             .Token("DockGroupView", "StripHeight", "40")
             // The new-tab menu is two panels wide.
             .Token("PopoverSurface", "PanelMaxWidth", "640");

    /// <summary>Saves the appearance and accent whenever the theme changes, from anywhere in the app. Safe to call twice.</summary>
    public void Track()
    {
        if (_tracker is not null)
        {
            return;
        }
        _tracker = (_, e) =>
        {
            var theme = Format(e.NewState.Appearance);
            var accent = e.NewState.Accent?.ToString();
            var current = settings.Current.Appearance;
            if (current.Theme == theme && current.Accent == accent)
            {
                return;
            }
            settings.Update(s =>
            {
                s.Appearance.Theme = theme;
                s.Appearance.Accent = accent;
            });
        };
        ArcTheme.Manager.StateChanged += _tracker;
    }

    /// <summary>Stops saving (the container that owns this service is gone).</summary>
    public void Dispose()
    {
        if (_tracker is not null)
        {
            ArcTheme.Manager.StateChanged -= _tracker;
            _tracker = null;
        }
    }

    public static ArcAppearance Parse(string? theme) => theme switch
    {
        "light" => ArcAppearance.Light,
        "dark" => ArcAppearance.Dark,
        _ => ArcAppearance.System
    };

    public static string Format(ArcAppearance appearance) => appearance switch
    {
        ArcAppearance.Light => "light",
        ArcAppearance.Dark => "dark",
        _ => "system"
    };
}
