using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Skia;
using Avalonia.Threading;
using Octopus.Mobile.Demo;
using Octopus.Mobile.Shell;
using Xunit;

// One UI thread: every test gets a fresh app on the headless platform, and the tests run one after another.
[assembly: AvaloniaTestApplication(typeof(Octopus.Mobile.Tests.TestApp))]
[assembly: AvaloniaTestFramework]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerTest)]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Octopus.Mobile.Tests;

/// <summary>The headless test host: the phone app on made-up hosts, drawn by Skia so that frames can be saved.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        // The text asserted on is the Chinese one, whatever the machine's language.
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
        return AppBuilder.Configure<MobileApp>()
                         .UseOctopusMobile()
                         .UseSkia()
                         .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

/// <summary>A phone-sized window with the app in it, on its own made-up workspace and account.</summary>
internal sealed class Phone : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(60);

    /// <param name="signInDelay">How long the made-up browser confirmation takes; a moment unless the test needs the sign-in page to stay.</param>
    /// <param name="pairingDelay">How long the made-up host takes to allow a pairing; a moment unless the test looks at the number first.</param>
    /// <param name="localOnly">Used without an account: signed out, with the computers such a phone has.</param>
    /// <param name="official">The build knows the official service; without one "Sign in" asks for an address.</param>
    /// <param name="updater">The app's updater; none (no update row) unless the test is about updates.</param>
    public Phone(bool signedIn = true, TimeSpan? signInDelay = null, TimeSpan? pairingDelay = null, bool localOnly = false, bool official = true,
                 Octopus.Mobile.Updates.MobileUpdater? updater = null)
    {
        Workspace = new DemoWorkspace(localOnly) { PairingDelay = pairingDelay ?? Short };
        Account = new DemoAccount { SignInDelay = signInDelay ?? Short, OfficialServer = official ? "https://octopus.example" : null };
        if (!signedIn || localOnly) Account.SignOut();
        if (localOnly) Account.UseLocalOnly();
        Shell = new MobileShell(Workspace, Account, uri => Opened = uri, updater);
        Window = new Window { Width = MobileApp.PreviewWidth, Height = MobileApp.PreviewHeight, Content = Shell };
        Window.Show();
        Pump();
    }

    public DemoWorkspace Workspace { get; }
    public DemoAccount Account { get; }
    public MobileShell Shell { get; }
    public Window Window { get; }
    /// <summary>The address the app last asked to open in the browser.</summary>
    public Uri? Opened { get; set; }

    /// <summary>The page on top, as the type the test expects.</summary>
    public T Top<T>() where T : class => Assert.IsType<T>(Shell.Top);

    /// <summary>Runs pending UI jobs and renders a few frames.</summary>
    public static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Renders frames in real time (Arc's motion and the made-up delays run on the clock) until the condition holds.</summary>
    public static bool Until(Func<bool> done, int milliseconds = 4000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            Pump();
            if (done()) return true;
            Thread.Sleep(16);
        }
        return false;
    }

    /// <summary>Lets a slide or a sheet come to rest.</summary>
    public static void Settle(int milliseconds = 1200) => Until(() => false, milliseconds);

    public void Dispose() => Window.Close();
}
