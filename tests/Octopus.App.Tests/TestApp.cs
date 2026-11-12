using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Skia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Octopus.App.Appearance;
using Octopus.App.Composition;
using Octopus.Agents;
using Octopus.App.Services;
using Octopus.Platform;
using Octopus.Terminal;
using Octopus.Storage;
using Xunit;

// Every test gets a fresh app on the headless platform, and the tests run one after another (Avalonia has one UI thread).
[assembly: AvaloniaTestApplication(typeof(Octopus.App.Tests.TestApp))]
[assembly: AvaloniaTestFramework]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]
[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: Octopus.App.Tests.FreshApp]

namespace Octopus.App.Tests;

/// <summary>The headless test host: the real app with in-memory stores and a temporary data folder.</summary>
public static class TestApp
{
    /// <summary>A new container (empty in-memory stores, a new data folder) for the test that is about to run.</summary>
    public static void Fresh()
    {
        AppHost.Reset();
        AppHost.Get<ThemeService>().Track();
    }

    /// <summary>Drops the container of the test that ran: watchers stop, the data folder goes.</summary>
    public static void Dispose() => AppHost.Reset();

    public static AppBuilder BuildAvaloniaApp()
    {
        // Tests must never read or change the person's own projects and settings.
        AppHost.Reset();
        // Plugins and the signal hub start only in the tests about them.
        Octopus.App.Plugins.PluginStartup.AutoStart = false;
        AppHost.ConfigureOverrides = services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IProjectStore, RecordingProjectStore>());
            services.AddSingleton(p => (RecordingProjectStore)p.GetRequiredService<IProjectStore>());
            services.Replace(ServiceDescriptor.Singleton<ISettingsStore, InMemorySettingsStore>());
            services.Replace(ServiceDescriptor.Singleton<IAppPaths>(_ => new TempAppPaths()));
            // Tests never touch the real keychain.
            services.Replace(ServiceDescriptor.Singleton<ISecretStore, MemorySecretStore>());
            // Plugins' agent data scopes (~/.claude, ~/.codex…) resolve under the temporary folder, never the real home.
            services.Replace(ServiceDescriptor.Singleton(p => new Octopus.Plugins.Security.UserHome(((TempAppPaths)p.GetRequiredService<IAppPaths>()).Home)));
            // No real process, no scan of the machine, no system dialogs.
            services.Replace(ServiceDescriptor.Singleton<ITerminalConnectionFactory, FakeTerminalConnectionFactory>());
            services.AddSingleton(p => (FakeTerminalConnectionFactory)p.GetRequiredService<ITerminalConnectionFactory>());
            services.Replace(ServiceDescriptor.Singleton<IAgentScanner>(new FakeAgentScanner("claude", "opencode", "codex")));
            services.Replace(ServiceDescriptor.Singleton<IDialogService, FakeDialogService>());
            services.AddSingleton(p => (FakeDialogService)p.GetRequiredService<IDialogService>());
            services.Replace(ServiceDescriptor.Singleton<IAppIcon, NoAppIcon>()); // never touch the real Dock
            services.Replace(ServiceDescriptor.Singleton<IPickerService, FakePickerService>());
            // The updater stays off in every configuration (CI tests a Release build): no request ever leaves a test.
            services.Replace(ServiceDescriptor.Singleton(new Octopus.Updates.UpdateOptions { CurrentVersion = null, IsDevelopment = true }));
            services.AddSingleton(p => (FakePickerService)p.GetRequiredService<IPickerService>());
        };
        return AppBuilder.Configure<App>()
                         .UseArcApp()
                         // Skia draws for real, so the screenshot tests can capture frames; nothing opens on screen.
                         .UseSkia()
                         .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }

    private sealed class TempAppPaths : IAppPaths, IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("octopus-tests-").FullName;
        public string DataDirectory => Path.Combine(_root, "data");
        public string CacheDirectory => Path.Combine(_root, "cache");
        /// <summary>A stand-in for the user's home, beside (not inside) the protected data directory.</summary>
        public string Home => Path.Combine(_root, "home");

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }
    }
}
