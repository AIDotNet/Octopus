using Avalonia;

namespace Octopus.App;

internal static class Program
{
    // Avalonia is not ready until Main runs: do not use any Avalonia or Arc API before BuildAvaloniaApp().
    [STAThread]
    public static void Main(string[] args)
    {
        if (Services.UninstallHookCleanup.TryHandleCommandLine(args))
        {
            return;
        }
        Services.CrashLog.Install();
        // On Windows & Linux, clicking octopus://auth-done in the browser spawns a new process; forward to the live window.
        if (Services.ProtocolActivation.TryParseProtocolUri(args) is { } uri
            && Services.ProtocolActivation.TryForwardToRunningInstance(uri))
        {
            return;
        }
        try
        {
            BuildAvaloniaApp()
                .AfterSetup(_ => Services.CrashLog.AttachDispatcher())
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Services.CrashLog.WriteCrash("Main", ex, isTerminating: true);
            throw;
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
