using System.Runtime.CompilerServices;
using Avalonia;
using Octopus.App.Appearance;
using Octopus.App.Composition;
using Octopus.App.Localization;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Fonts;
using TKCode.Arc.Themes.Corners;

namespace Octopus.App;

/// <summary>
/// How the app sets up Avalonia and Arc, in one place: every host (the desktop Program.cs, a test host) starts from
/// <c>AppBuilder.Configure&lt;App&gt;()</c>, picks its platform and calls <see cref="UseArcApp"/>.
/// </summary>
public static class AppSetup
{
    // TKCode.Arc.Special 1.3.2-preview.5 does not yet expose UseBranchGraph() on ArcSpecialBuilder.
    // Calling the generated theme factory directly via .NET 10 UnsafeAccessorType keeps NativeAOT trimming
    // able to drop the other ~124 unused Special controls instead of rooting the entire 8.9 MB assembly.
    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "BranchGraphTheme")]
    private static extern ArcThemeAsset CreateBranchGraphThemeAsset(
        [UnsafeAccessorType("TKCode.Arc.Generated.TKCodeArcSpecial.ArcGeneratedThemeAssets, TKCode.Arc.Special")] object? target);

    /// <summary>Fonts, Arc's packages and the theme of the first frame.</summary>
    public static AppBuilder UseArcApp(this AppBuilder builder) =>
        builder.WithInterFont()
               .WithArcFonts()
               .UseArc(arc => arc
                   .UseLanguages(AppLanguages.Initial, AppLanguages.Supported)
                   .UseArcMarkdown()     // Markdown rendering for AgentTimelineView, MarkdownViewerView and UpdateNotesView
                   .UseArcBlocks(static blocks => blocks.UseSettings().UseSidebar()) // trim-safe selective Blocks (Settings + Sidebar only)
                   .UseArcSpecial(static special => special.UseImageReveal())        // trim-safe selective Special (ImageReveal)
                   .UsePackage("TKCode.Arc.Special.BranchGraph", static p => p       // only BranchGraph theme + tokens instead of all 126 Special controls
                       .AddTokens([BranchGraphTokens.Descriptor])
                       .AddThemes([CreateBranchGraphThemeAsset(null)]))
                   .UseArcHighlighting() // TextMate syntax highlighting for CodeBlock, the Markdown editor and fenced code
                   .UseArcCodeEditor()   // Monaco-based code editor for editable source and config file tabs
                   .UseArcDock()         // the workspace: tab groups and splits
                   .UseArcCorners()      // compact corners: the whole app is a dense tool, not a landing page
                   .UsePackage("Octopus", static p => TKCode.Arc.Generated.Octopus.ArcGeneratedAssets.AddTo(p)) // own text + Arc controls
                   .WithTheme(AppHost.Get<ThemeService>().ApplyStartupTheme)); // saved appearance + accent, before the first frame
}
