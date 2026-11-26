using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Octopus.Mobile.Demo;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;
using TKCode.Arc;
using TKCode.Arc.Fonts;
using TKCode.Arc.Localization;
using TKCode.Arc.Mobile;

namespace Octopus.Mobile;

/// <summary>
/// What the app runs on: the hosts it shows and the account it is signed in with. A platform head sets these before
/// Avalonia starts, from a <see cref="Remote.MobileRemote"/> on its own keystore (design/12-远程操作.md §9.6); left
/// alone, the app shows made-up data (<see cref="DemoWorkspace"/>), which the layout tests and "--demo" use.
/// </summary>
public static class MobileHost
{
    public static IRemoteWorkspace Workspace { get; set; } = new DemoWorkspace();
    public static IAccountSession Account { get; set; } = new DemoAccount();
    /// <summary>Opens the sign-in page in the system browser; the head knows how.</summary>
    public static Action<Uri> OpenBrowser { get; set; } = _ => { };
    /// <summary>The app's updater; only a head that can install a package sets one (Android). Elsewhere: no update row.</summary>
    public static Updates.MobileUpdater Updater { get; set; } = Updates.MobileUpdater.Unsupported;

    /// <summary>
    /// Lets the person choose one picture (<see cref="Terminals.ImagePicking"/>: the photo library on iOS, the system's
    /// picker on Android); tests put their own picture here. Null when nothing was chosen.
    /// </summary>
    public static Func<Avalonia.Controls.TopLevel?, Task<Terminals.PickedImage?>> PickImage { get; set; } = Terminals.ImagePicking.PickWithStorageAsync;

    /// <summary>
    /// Lets the person choose any file (<see cref="Terminals.ImagePicking.PickFileWithStorageAsync"/>) for GUI
    /// conversation attachments; tests put their own file here. Null when nothing was chosen.
    /// </summary>
    public static Func<Avalonia.Controls.TopLevel?, Task<Terminals.PickedImage?>> PickFile { get; set; } = Terminals.ImagePicking.PickFileWithStorageAsync;

    /// <summary>
    /// Opens the platform's native QR code scanner and returns the decoded text, or null when cancelled.
    /// Set by the iOS and Android platform heads (and by tests); null on heads without a camera (desktop preview).
    /// </summary>
    public static Func<Task<string?>>? ScanQrCode { get; set; }

    /// <summary>
    /// Voice input (design/ui/移动端输入框重写与语音输入方案.html): a head makes one with the phone's own recognizer and
    /// microphone. The default has neither, so the microphone button stays hidden (tests, the desktop preview).
    /// </summary>
    public static Voice.VoiceService Voice { get; set; } = new();

    /// <summary>A tap the phone can feel; the heads with haptics set it.</summary>
    public static Action<Voice.HapticKind> Haptic { get; set; } = _ => { };

    private static readonly object DeepLinkGate = new();
    private static Uri? s_pendingDeepLink;

    /// <summary>Raised when an actionable <c>octopus://pair</c> or <c>octopus://relay</c> deep link arrives.</summary>
    public static event Action<Uri>? DeepLinkReceived;

    /// <summary>True while the mobile app is backgrounded by the OS.</summary>
    public static bool IsBackgrounded => Volatile.Read(ref s_backgrounded) != 0;
    private static int s_backgrounded;

    /// <summary>Raised when the mobile app moves to the background (timers can pause).</summary>
    public static event Action? Backgrounded;

    /// <summary>Raised when the mobile app returns to the foreground (timers resume and connections are probed).</summary>
    public static event Action? Foregrounded;

    /// <summary>Signals that the OS moved the app to the background; idempotent when already backgrounded.</summary>
    public static void NotifyBackgrounded()
    {
        if (Interlocked.Exchange(ref s_backgrounded, 1) != 0) return;
        Workspace.OnAppBackgrounded();
        Backgrounded?.Invoke();
    }

    /// <summary>Signals that the app returned to the foreground; probes stale connections and resumes timers.</summary>
    public static void NotifyForegrounded()
    {
        if (Interlocked.Exchange(ref s_backgrounded, 0) == 0) return;
        Workspace.OnAppForegrounded();
        Foregrounded?.Invoke();
    }

    /// <summary>
    /// Handles an incoming <c>octopus://</c> deep link: <c>octopus://auth-done</c> after browser sign-in,
    /// <c>octopus://pair?…</c> for direct LAN/relay pairing (for example scanned by the system camera), or
    /// <c>octopus://relay?…</c> for self-hosted relay setup.
    /// </summary>
    public static void HandleDeepLink(Uri uri)
    {
        if (!string.Equals(uri.Scheme, "octopus", StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(uri.Host, "auth-done", StringComparison.OrdinalIgnoreCase))
        {
            if (Account.IsSignedIn) _ = Workspace.RefreshAsync();
            return;
        }
        if (string.Equals(uri.Host, "pair", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "relay", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Host, "bot", StringComparison.OrdinalIgnoreCase))
        {
            Action<Uri>? handlers;
            lock (DeepLinkGate)
            {
                handlers = DeepLinkReceived;
                if (handlers is null) s_pendingDeepLink = uri;
            }
            handlers?.Invoke(uri);
        }
    }

    internal static Uri? TakePendingDeepLink()
    {
        lock (DeepLinkGate)
        {
            var pending = s_pendingDeepLink;
            s_pendingDeepLink = null;
            return pending;
        }
    }
}

/// <summary>
/// The phone application. On a phone the shell is the single view; started on a desktop (the preview head) it sits in a
/// window the size of a phone.
/// </summary>
public class MobileApp : Application
{
    /// <summary>The preview window: a current phone's size in points.</summary>
    public const double PreviewWidth = 390;
    public const double PreviewHeight = 844;

    public MobileApp() => Name = "Octopus";

    public override void OnFrameworkInitializationCompleted()
    {
        // Created through its class, not loaded by URI: a trimmed (NativeAOT) build does not keep the URI lookup.
        Styles.Add(new MobileStyles());
        switch (ApplicationLifetime)
        {
            case ISingleViewApplicationLifetime single:
                single.MainView = CreateShell();
                break;
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new Window
                {
                    Title = "Octopus",
                    Width = PreviewWidth,
                    Height = PreviewHeight,
                    CanResize = true,
                    Content = CreateShell(),
                };
                break;
        }
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
        {
            activatable.Activated += (_, e) =>
            {
                if (e is ProtocolActivatedEventArgs { Uri: { } uri }) MobileHost.HandleDeepLink(uri);
                else if (e.Kind == ActivationKind.Background) MobileHost.NotifyForegrounded();
            };
            activatable.Deactivated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Background) MobileHost.NotifyBackgrounded();
            };
        }
        // Once per start and quietly: a newer version shows in the "Me" tab; nothing downloads without a tap.
        _ = MobileHost.Updater.CheckQuietlyAsync();
        base.OnFrameworkInitializationCompleted();
    }

    public static MobileShell CreateShell() => new(MobileHost.Workspace, MobileHost.Account, MobileHost.OpenBrowser, MobileHost.Updater);
}

/// <summary>How every head (a phone, the desktop preview, a test host) sets up Avalonia and Arc for the phone app.</summary>
public static class MobileSetup
{
    /// <summary>Fonts, Arc's touch controls and the app's own text. The theme follows the system.</summary>
    public static AppBuilder UseOctopusMobile(this AppBuilder builder) =>
        builder.WithInterFont()
               .WithArcFonts()
               .UseArc(arc => arc
                   .UseLanguages(InitialLanguage, [LanguageTags.ZhCN, LanguageTags.EnUS])
                   .UseArcMobile()
                   // The app's own package: its text (Localization/).
                   .UsePackage("Octopus.Mobile", static p => TKCode.Arc.Generated.OctopusMobile.ArcGeneratedAssets.AddTo(p)));

    /// <summary>Simplified Chinese on a Chinese system, English anywhere else.</summary>
    private static LanguageTag InitialLanguage =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? LanguageTags.ZhCN : LanguageTags.EnUS;
}
