using System.Net;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Octopus.Mobile.Remote;
using Octopus.Remote.Relay;
using Xamarin.Android.Net;

namespace Octopus.Mobile.Android;

/// <summary>
/// The Android process. Before Avalonia starts, the app is given the real hosts and account, on this phone's keystore
/// (<see cref="KeystoreMobileStore"/>); the pages are the shared ones of src/Octopus.Mobile.
/// </summary>
[Application]
public sealed class AndroidApp : AvaloniaAndroidApplication<MobileApp>
{
    public AndroidApp(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    public override void OnCreate()
    {
        // Route relay and update HTTP requests through Android's native Java/Conscrypt stack so HarmonyOS / OEM
        // certificate stores, NetworkSecurityConfig, and system proxies are respected consistently.
        RelayHttp.HandlerFactory = static automaticDecompression => new AndroidMessageHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = automaticDecompression ? DecompressionMethods.All : DecompressionMethods.None,
        };
        ConfigureWebSocketRoots();
        UseRealHosts(this);
        base.OnCreate();
    }

    private void ConfigureWebSocketRoots()
    {
        try
        {
            using var stream = Resources?.OpenRawResource(Resource.Raw.ca_roots);
            if (stream is null) return;
            using var reader = new StreamReader(stream);
            var roots = RelayHttp.LoadPemCertificates(reader.ReadToEnd());
            if (roots.Count > 0)
            {
                RelayHttp.WebSocketCertificateValidationCallback = RelayHttp.CreateCustomRootsValidator(roots);
            }
        }
        catch
        {
            // Never block app startup if the bundled root CA resource cannot be read.
        }
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder)
            .ConfigureFonts(fm =>
            {
                // Replace Avalonia 12.2's SystemFontCollection before anything uses it so character-matched unnamed
                // Android fallback fonts (variable NotoSansCJK-Regular.ttc, symbols like ⎿/✽/✓, Noto Sans Old Italic,
                // etc.) always resolve through Typeface.GlyphTypeface instead of throwing InvalidOperationException.
                fm.AddFontCollection(new AndroidSystemFontCollection(fm.SystemFonts));
            })
            .UseOctopusMobile()
            .AfterSetup(_ =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(HookAndroidKeyboardInset, Avalonia.Threading.DispatcherPriority.Loaded);
            });

    /// <summary>
    /// Avalonia.Android's <see cref="Avalonia.Controls.Platform.IInputPane"/> subtracts the navigation bar inset from
    /// <c>EndRect.Height</c> while positioning <c>EndRect.Y</c> above both the keyboard and the navigation bar. On
    /// edge-to-edge Android 15+ this leaves <c>Arc.Mobile.Page.AvoidKeyboard</c> short by the gesture bar's height.
    /// Feed the true bottom occlusion (<c>ClientSize.Height - EndRect.Y</c>) through <c>Page.KeyboardInsetSource</c>.
    /// </summary>
    private static void HookAndroidKeyboardInset()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime { MainView: { } view })
        {
            return;
        }
        // Always subscribe to AttachedToVisualTree so if Android recreates MainActivity with a new TopLevel,
        // keyboard inset tracking re-binds cleanly instead of holding a stale TopLevel reference.
        view.AttachedToVisualTree += (_, _) => AttachAndroidKeyboardInset(view);
        if (Avalonia.Controls.TopLevel.GetTopLevel(view) is not null)
        {
            AttachAndroidKeyboardInset(view);
        }
    }

    private static Avalonia.Controls.TopLevel? s_insetTopLevel;

    private static void AttachAndroidKeyboardInset(Avalonia.Controls.Control view)
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(view);
        var pane = topLevel?.InputPane;
        if (topLevel is null || pane is null || ReferenceEquals(s_insetTopLevel, topLevel)) return;
        s_insetTopLevel = topLevel;

        var source = new TKCode.Arc.Mobile.KeyboardInsetSource();
        TKCode.Arc.Mobile.Page.SetKeyboardInsetSource(view, source);

        double TrueInset(Avalonia.Controls.Platform.InputPaneState state, Avalonia.Rect rect)
        {
            if (state != Avalonia.Controls.Platform.InputPaneState.Open || rect.Height <= 0) return 0;
            var safeBottom = topLevel.InsetsManager?.SafeAreaPadding.Bottom ?? 0;
            var fromBottom = topLevel.ClientSize.Height - rect.Y;
            return Math.Max(rect.Height + safeBottom, fromBottom > 0 ? fromBottom : rect.Height);
        }

        source.Set(TrueInset(pane.State, pane.OccludedRect), TimeSpan.Zero);
        pane.StateChanged += (_, e) => source.Set(TrueInset(e.NewState, e.EndRect), e.AnimationDuration);
    }

    private static void UseRealHosts(Context context)
    {
        // The app's own files folder: no other app can read it, and it goes with the app when it is removed.
        var directory = Path.Combine(context.FilesDir!.AbsolutePath, "octopus");
        var store = new KeystoreMobileStore(directory);
        var remote = new MobileRemote(store, DeviceName());
        remote.Load();
        MobileHost.Workspace = remote.Workspace;
        MobileHost.Account = remote.Account;
        MobileHost.OpenBrowser = address => OpenBrowser(context, address);
        MobileHost.ScanQrCode = () => AndroidQrScanner.ScanAsync(context);
        MobileHost.Voice = new Voice.VoiceService(store, new AndroidSpeechDictation(context), () => new AndroidAudioCapture(),
            account: remote.GetServerAsync, signedIn: () => remote.Account.IsSignedIn);
        MobileHost.Haptic = AndroidHaptics.Perform;
        MobileHost.Updater = Updates.MobileUpdater.Create(new AndroidUpdatePlatform(context), MobileHost.OpenBrowser);
    }

    /// <summary>What a computer shows for this phone: "Google Pixel 9", the maker and the model.</summary>
    private static string DeviceName()
    {
        var maker = Build.Manufacturer ?? "";
        var model = Build.Model ?? "Android";
        return model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) || maker.Length == 0
            ? model
            : $"{char.ToUpperInvariant(maker[0])}{maker[1..]} {model}";
    }

    // Only ever a web address (the sign-in page), handed to the system's browser.
    private static void OpenBrowser(Context context, Uri address)
    {
        if (address.Scheme is not ("https" or "http")) return;
        var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(address.AbsoluteUri));
        intent.AddFlags(ActivityFlags.NewTask);
        try { context.StartActivity(intent); }
        catch (ActivityNotFoundException) { }
    }
}

[Activity(Label = "Octopus", MainLauncher = true, Icon = "@mipmap/octopus", Theme = "@style/Theme.AppCompat.DayNight.NoActionBar",
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density | ConfigChanges.FontScale | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden,
    WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = "octopus")]
public sealed class MainActivity : AvaloniaMainActivity
{
    internal static WeakReference<MainActivity>? Current { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = new WeakReference<MainActivity>(this);
        DispatchDeepLink(Intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        Current = new WeakReference<MainActivity>(this);
        MobileHost.NotifyForegrounded();
    }

    protected override void OnStop()
    {
        base.OnStop();
        // Launching our own full-screen QrScannerActivity covers MainActivity without leaving the app;
        // suppress false background/foreground churn so returning doesn't trigger a full reconnect storm.
        if (!AndroidQrScanner.IsActive)
        {
            MobileHost.NotifyBackgrounded();
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        AndroidPermissions.Complete(requestCode, grantResults);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        DispatchDeepLink(intent);
    }

    private static void DispatchDeepLink(Intent? intent)
    {
        if (intent?.DataString is { Length: > 0 } raw && Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            MobileHost.HandleDeepLink(uri);
        }
    }
}
