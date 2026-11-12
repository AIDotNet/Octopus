using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Octopus.App.Appearance;
using Octopus.App.Composition;
using Octopus.App.Services;
using Octopus.App.Shell;
using Octopus.Agents;

namespace Octopus.App;

/// <summary>
/// The application. Arc needs nothing here: no base class, no App.axaml and no styles. UseArcApp() (AppSetup.cs)
/// registers the themes. Services come from <see cref="AppHost"/>.
/// </summary>
public class App : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        Styles.Add(new StyleInclude(new Uri("avares://Octopus/")) { Source = new Uri("avares://Octopus/Common/AppStyles.axaml") });
        AppHost.Get<ThemeService>().Track();
        // The projects are read before the first frame; the agent scan runs in the background (its cached result first).
        // Task.Run: this thread has Avalonia's SynchronizationContext, so awaiting LoadAsync here would post its
        // continuation back to this (blocked) thread and deadlock before the window is ever created.
        var projects = AppHost.Get<IProjectService>();
        Task.Run(projects.LoadAsync).GetAwaiter().GetResult();
        _ = StartAgentScanAsync();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
            // The window is gone and everything saved: let go of what the services hold (file watchers, timers).
            desktop.Exit += (_, _) => AppHost.Reset();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartAgentScanAsync()
    {
        try
        {
            var scanner = AppHost.Get<IAgentScanner>();
            await scanner.LoadCacheAsync();
            await scanner.ScanAsync();
        }
        catch
        {
            // A failed scan only means the launcher lists no agents.
        }
    }
}
