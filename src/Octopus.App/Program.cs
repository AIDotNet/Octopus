using Avalonia;

namespace Octopus.App;

internal static class Program
{
    // Avalonia is not ready until Main runs: do not use any Avalonia or Arc API before BuildAvaloniaApp().
    [STAThread]
    public static void Main(string[] args)
    {
        Services.StartupTrace.Install(args);
        Services.StartupTrace.Log("main:enter");
        if (Services.UninstallHookCleanup.TryHandleCommandLine(args))
        {
            Services.StartupTrace.Log("main:uninstall-hooks-command");
            return;
        }
        Services.CrashLog.Install();
        Services.StartupTrace.Log("crashlog:installed");
        // On Windows & Linux, clicking octopus://auth-done in the browser spawns a new process; forward to the live window.
        if (Services.ProtocolActivation.TryParseProtocolUri(args) is { } uri
            && Services.ProtocolActivation.TryForwardToRunningInstance(uri))
        {
            Services.StartupTrace.Log("main:forwarded-protocol", uri.ToString());
            return;
        }
        try
        {
            Services.StartupTrace.Log("avalonia:build");
            BuildAvaloniaApp()
                .AfterSetup(_ =>
                {
                    Services.StartupTrace.Log("avalonia:after-setup");
                    Services.CrashLog.AttachDispatcher();
                })
                .StartWithClassicDesktopLifetime(args);
            Services.StartupTrace.Log("lifetime:returned");
        }
        catch (Exception ex)
        {
            Services.StartupTrace.LogException("main:catch", ex);
            Services.CrashLog.WriteCrash("Main", ex, isTerminating: true);
            throw;
        }
        finally
        {
            Services.StartupTrace.Log("main:end");
        }
    }

    // Also used by the XAML previewer of your IDE. UseArcApp() (AppSetup.cs) sets up the fonts, Arc and the theme.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
                                .UsePlatformDetect()
                                .With(new SkiaOptions { MaxGpuResourceSizeBytes = 48 * 1024 * 1024 })
                                .UseArcApp();
#if DEBUG
        builder = builder.LogToTrace();
#endif
        return builder;
    }
}
