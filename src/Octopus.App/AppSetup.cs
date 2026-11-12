using Avalonia;
using Octopus.App.Appearance;
using Octopus.App.Composition;
using Octopus.App.Localization;
using TKCode.Arc;
using TKCode.Arc.Fonts;
using TKCode.Arc.Themes.Corners;

namespace Octopus.App;

/// <summary>
/// How the app sets up Avalonia and Arc, in one place: every host (the desktop Program.cs, a test host) starts from
/// <c>AppBuilder.Configure&lt;App&gt;()</c>, picks its platform and calls <see cref="UseArcApp"/>.
/// </summary>
public static class AppSetup
{
    /// <summary>Fonts, Arc's packages and the theme of the first frame.</summary>
    public static AppBuilder UseArcApp(this AppBuilder builder) =>
        builder.WithInterFont()
               .WithArcFonts()
               .UseArc(arc => arc
                   .UseLanguages(AppLanguages.Initial, AppLanguages.Supported)
                   .UseArcBlocks()   // Arc's controls and blocks (SettingsPage); registers UseArcControls() too
                   .UseArcDock()     // the workspace: tab groups and splits
                   .UseArcCorners()  // compact corners: the whole app is a dense tool, not a landing page
                   // The app's own package: its text (Localization/) and any Arc controls of its own.
                   .UsePackage("Octopus", static p => TKCode.Arc.Generated.Octopus.ArcGeneratedAssets.AddTo(p))
                   // The saved appearance and accent, read from settings.json before the first frame.
                   .WithTheme(AppHost.Get<ThemeService>().ApplyStartupTheme));
}
