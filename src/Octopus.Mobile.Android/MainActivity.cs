using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Octopus.Mobile.Remote;

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
        UseRealHosts(this);
        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder)
            .UseOctopusMobile()
            .With(new Avalonia.Media.FontManagerOptions
            {
                // Avalonia 12.2's SystemFontCollection misses Normal-weight lookup for unnamed Android CJK families
                // (such as Android 15's variable NotoSansCJK-Regular.ttc); map CJK ranges through the system matcher.
                FontFallbacks =
                [
                    new Avalonia.Media.FontFallback
                    {
                        FontFamily = new Avalonia.Media.FontFamily("fonts:AndroidCjk#AndroidCjk"),
                        UnicodeRange = Avalonia.Media.UnicodeRange.Parse("U+2E80-9FFF, U+F900-FAFF, U+FE30-FE4F, U+FF00-FFEF"),
                    },
                ],
            })
            .ConfigureFonts(fm =>
            {
                var cjk = new AndroidCjkFontCollection();
                cjk.Seed(fm);
                fm.AddFontCollection(cjk);
            })
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
        if (Avalonia.Controls.TopLevel.GetTopLevel(view) is not null)
        {
            AttachAndroidKeyboardInset(view);
            return;
        }
        view.AttachedToVisualTree += OnAttached;
        void OnAttached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
        {
            view.AttachedToVisualTree -= OnAttached;
            AttachAndroidKeyboardInset(view);
        }
    }

    private static void AttachAndroidKeyboardInset(Avalonia.Controls.Control view)
    {
        var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(view);
        var pane = topLevel?.InputPane;
        if (topLevel is null || pane is null) return;

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

    private sealed class AndroidCjkFontCollection : Avalonia.Media.Fonts.FontCollectionBase
    {
        private const string Family = "AndroidCjk";
        public override Uri Key { get; } = new("fonts:AndroidCjk", UriKind.Absolute);

        public void Seed(Avalonia.Media.FontManager fm)
        {
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            var addedAny = false;
            // Asking for Medium/SemiBold/Bold populates SystemFonts via BuildTypefaceWithSynthesis and yields Android's
            // exact system CJK GlyphTypeface (e.g. Noto Sans CJK SC on Android 15); map it for Normal as well.
            foreach (var weight in new[] { Avalonia.Media.FontWeight.Medium, Avalonia.Media.FontWeight.SemiBold, Avalonia.Media.FontWeight.Bold })
            {
                if (!fm.TryMatchCharacter('中', Avalonia.Media.FontStyle.Normal, weight, Avalonia.Media.FontStretch.Normal, null, culture, out var matched)
                    || !fm.TryGetGlyphTypeface(matched, out var glyph))
                {
                    continue;
                }
                if (!addedAny)
                {
                    TryAddGlyphTypeface(Family, new Avalonia.Media.Fonts.FontCollectionKey(Avalonia.Media.FontStyle.Normal, Avalonia.Media.FontWeight.Normal, Avalonia.Media.FontStretch.Normal), glyph);
                }
                TryAddGlyphTypeface(Family, new Avalonia.Media.Fonts.FontCollectionKey(Avalonia.Media.FontStyle.Normal, weight, Avalonia.Media.FontStretch.Normal), glyph);
                addedAny = true;
            }
            if (addedAny) AddFontFamily(new Avalonia.Media.FontFamily(null, $"{Key.AbsoluteUri}#{Family}"));
        }

        public override bool TryGetGlyphTypeface(string familyName, Avalonia.Media.FontStyle style, Avalonia.Media.FontWeight weight, Avalonia.Media.FontStretch stretch, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Avalonia.Media.GlyphTypeface? glyphTypeface) =>
            TryGetGlyphTypeface(Family, new Avalonia.Media.Fonts.FontCollectionKey(style, weight, stretch), allowNearestMatch: true, out glyphTypeface);
    }

    private static void UseRealHosts(Context context)
    {
        // The app's own files folder: no other app can read it, and it goes with the app when it is removed.
        var directory = Path.Combine(context.FilesDir!.AbsolutePath, "octopus");
        var remote = new MobileRemote(new KeystoreMobileStore(directory), DeviceName());
        remote.Load();
        MobileHost.Workspace = remote.Workspace;
        MobileHost.Account = remote.Account;
        MobileHost.OpenBrowser = address => OpenBrowser(context, address);
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
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize,
    WindowSoftInputMode = global::Android.Views.SoftInput.AdjustResize)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable], DataScheme = "octopus")]
public sealed class MainActivity : AvaloniaMainActivity
{
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent?.DataString is { Length: > 0 } raw && Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            MobileHost.HandleDeepLink(uri);
        }
    }
}
