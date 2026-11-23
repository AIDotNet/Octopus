using Avalonia;
using Avalonia.Media;
using Octopus.Core.Settings;
using Octopus.Storage;
using TKCode.Arc;

namespace Octopus.App.Appearance;

/// <summary>Keeps Arc's theme, layout style (density) and settings.json in step: applies saved choices at start-up and saves every change.</summary>
public sealed class ThemeService(ISettingsStore settings) : IDisposable
{
    private static UiDensityMetrics s_appliedMetrics = UiDensityMetrics.Compact;

    private EventHandler<ArcThemeChangedEventArgs>? _tracker;

    /// <summary>The layout style currently applied to the application (<see cref="UiDensityMetrics.Compact"/> by default).</summary>
    public static UiDensityMetrics CurrentMetrics => s_appliedMetrics;

    /// <summary>The current layout style key (<c>compact</c> or <c>comfortable</c>).</summary>
    public string Density => UiDensityMetrics.Normalize(settings.Current.Appearance.Density);

    /// <summary>Metrics for the current layout style.</summary>
    public UiDensityMetrics Metrics => UiDensityMetrics.For(settings.Current.Appearance.Density);

    /// <summary>Raised on the UI thread after the layout style changes.</summary>
    public event EventHandler<UiDensityMetrics>? DensityChanged;

    /// <summary>The theme of the first frame (passed to <c>WithTheme</c>).</summary>
    public void ApplyStartupTheme(ArcThemeBuilder theme)
    {
        var appearance = settings.Current.Appearance;
        var metrics = UiDensityMetrics.For(appearance.Density);
        s_appliedMetrics = metrics;
        ConfigureWorkspace(theme, metrics);
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
    /// Compact corners, density-appropriate Arc tokens, and a dock layout that starts at the window's top edge: no padding
    /// around it, a thin gap between groups, and tab strips as tall as the top row (design 03 section 1.1).
    /// </summary>
    public static void ConfigureWorkspace(ArcThemeBuilder theme, UiDensityMetrics? metrics = null) =>
        (metrics ?? UiDensityMetrics.Compact).ApplyToArc(theme);

    /// <summary>Writes the current layout style's dynamic resources into <see cref="Application.Current"/>.</summary>
    public void ApplyDensityResources()
    {
        var metrics = Metrics;
        s_appliedMetrics = metrics;
        if (Application.Current is { } app)
        {
            metrics.WriteResources(app.Resources);
        }
    }

    /// <summary>Switches the interface layout style (<c>compact</c> or <c>comfortable</c>), applies it live and persists it.</summary>
    public void SetDensity(string? density)
    {
        var normalized = UiDensityMetrics.Normalize(density);
        var metrics = UiDensityMetrics.For(normalized);
        var savedChanged = !string.Equals(settings.Current.Appearance.Density, normalized, StringComparison.Ordinal);
        if (savedChanged)
        {
            settings.Update(s => s.Appearance.Density = normalized);
        }
        var appliedChanged = !ReferenceEquals(s_appliedMetrics, metrics);
        s_appliedMetrics = metrics;
        if (Application.Current is { } app)
        {
            metrics.WriteResources(app.Resources);
        }
        if (appliedChanged || savedChanged)
        {
            ArcTheme.Apply(t => ConfigureWorkspace(t, metrics));
            DensityChanged?.Invoke(this, metrics);
        }
    }

    /// <summary>Saves the appearance and accent whenever the theme changes, and ensures density resources match settings. Safe to call twice.</summary>
    public void Track()
    {
        var metrics = Metrics;
        var needsArcSync = !ReferenceEquals(s_appliedMetrics, metrics);
        s_appliedMetrics = metrics;
        if (Application.Current is { } app)
        {
            metrics.WriteResources(app.Resources);
        }
        if (needsArcSync)
        {
            ArcTheme.Apply(t => ConfigureWorkspace(t, metrics));
        }

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
