using Android;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace Octopus.Mobile.Android;

/// <summary>
/// Launches <see cref="QrScannerActivity"/> and resolves with the decoded QR code text, or null when cancelled.
/// Uses Android Camera2 + pure managed <see cref="QRCodeReader"/> so scanning works offline on every Android device
/// with zero Google Play Services dependency.
/// </summary>
internal static class AndroidQrScanner
{
    private static readonly object Gate = new();
    private static TaskCompletionSource<string?>? s_pending;
    private static int s_active;

    /// <summary>
    /// True while <see cref="QrScannerActivity"/> is launching or visible on top of <see cref="MainActivity"/>,
    /// used to suppress false background/foreground reconnect storms in <see cref="MainActivity.OnStop"/>.
    /// </summary>
    internal static bool IsActive => Volatile.Read(ref s_active) != 0;

    public static Task<string?> ScanAsync(Context fallbackContext)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string?>? previous;
        lock (Gate)
        {
            previous = s_pending;
            s_pending = tcs;
        }
        previous?.TrySetResult(null);

        var host = MainActivity.Current is { } weak && weak.TryGetTarget(out var activity) && !activity.IsFinishing && !activity.IsDestroyed
            ? activity
            : fallbackContext;

        var intent = new Intent(host, typeof(QrScannerActivity));
        if (host is not Activity) intent.AddFlags(ActivityFlags.NewTask);
        Volatile.Write(ref s_active, 1);
        try
        {
            host.StartActivity(intent);
        }
        catch
        {
            Volatile.Write(ref s_active, 0);
            Complete(null);
        }
        return tcs.Task;
    }

    internal static void MarkClosed() => Volatile.Write(ref s_active, 0);

    internal static void Complete(string? value)
    {
        TaskCompletionSource<string?>? pending;
        lock (Gate)
        {
            pending = s_pending;
            s_pending = null;
        }
        pending?.TrySetResult(value);
    }
}

/// <summary>
/// Full-screen native Android Camera2 QR scanner built on <see cref="SurfaceView"/> (zero-GPU-copy HWC overlay),
/// unsafe zero-allocation direct-buffer ROI extraction, async Camera2 open/close serialization, AE 30fps preference,
/// black-frame skipping, tap-to-focus, and decoupled background ZXing decoding.
/// </summary>
[Activity(
    Label = "Scan QR Code",
    Theme = "@android:style/Theme.Black.NoTitleBar.Fullscreen",
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class QrScannerActivity : Activity, ISurfaceHolderCallback, ImageReader.IOnImageAvailableListener
{
    private const int RequestCameraCode = 47601;
    private const long DecodeIntervalMs = 150;
    private const float RoiFraction = 0.78f;
    private const int MinLumaContrast = 18;

    private sealed record CameraProfile(
        string CameraId,
        bool HasFlash,
        bool SupportsContinuousAf,
        bool SupportsAutoAf,
        int MaxAfRegions,
        int SensorWidth,
        int SensorHeight,
        int PreviewWidth,
        int PreviewHeight,
        int AnalysisWidth,
        int AnalysisHeight,
        int FpsMin,
        int FpsMax);

    // Static hardware profile cache: avoids repeated CameraManager Binder IPC on subsequent scans.
    private static CameraProfile? s_cachedProfile;
    // Serializes asynchronous camera teardown with any immediate subsequent open so we never hit CAMERA_IN_USE.
    private static readonly SemaphoreSlim CameraHardwareGate = new(1, 1);

    private readonly object _cameraLock = new();
    private QRCodeReader? _reader;
    private readonly Dictionary<DecodeHintType, object> _fastHints = new()
    {
        [DecodeHintType.CHARACTER_SET] = "UTF-8",
        [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
    };
    private readonly Dictionary<DecodeHintType, object> _harderHints = new()
    {
        [DecodeHintType.TRY_HARDER] = true,
        [DecodeHintType.CHARACTER_SET] = "UTF-8",
        [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
    };

    private FrameLayout? _root;
    private SurfaceView? _surfaceView;
    private ViewfinderOverlayView? _overlay;
    private Button? _torchButton;
    private HandlerThread? _cameraThread;
    private Handler? _cameraHandler;
    private CameraDevice? _cameraDevice;
    private CameraCaptureSession? _captureSession;
    private CaptureRequest.Builder? _previewBuilder;
    private ImageReader? _imageReader;
    private DeviceStateCallback? _deviceCallback;
    private SessionStateCallback? _sessionCallback;
    private CameraProfile? _activeProfile;
    private bool _surfaceReady;
    private bool _cameraOpening;
    private bool _permissionRequested;
    private int _previewWidth = 1280;
    private int _previewHeight = 720;
    private bool _hasFlash;
    private bool _torchOn;
    private long _lastDecodeMs;
    private int _decodeCounter;
    private int _decoding;
    private byte[]? _roiBuffer;
    private byte[]? _fallbackFlatBuffer;
    private int _finished;
    private int _openGeneration;

    private static bool IsChinese =>
        Java.Util.Locale.Default?.Language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var root = new FrameLayout(this)
        {
            LayoutParameters = new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent),
        };
        root.SetBackgroundColor(Color.Black);
        _root = root;

        // SurfaceView uses a dedicated HWC overlay plane (zero GPU compositing copy and lower latency than TextureView).
        var surfaceView = new SurfaceView(this)
        {
            LayoutParameters = new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent,
                GravityFlags.Center),
        };
        surfaceView.Holder?.AddCallback(this);
        surfaceView.Touch += OnSurfaceTouch;
        root.AddView(surfaceView);
        _surfaceView = surfaceView;

        var overlay = new ViewfinderOverlayView(this, IsChinese
            ? "将电脑上的配对二维码放入框内"
            : "Align the computer's pairing QR code inside the frame")
        {
            LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent),
        };
        root.AddView(overlay);
        _overlay = overlay;

        var density = Resources?.DisplayMetrics?.Density ?? 2f;
        int Dp(int v) => (int)(v * density + 0.5f);

        var close = CreateTopButton(IsChinese ? "取消" : "Cancel", Dp);
        var closeParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(40), GravityFlags.Top | GravityFlags.Start)
        {
            LeftMargin = Dp(16),
            TopMargin = Dp(44),
        };
        close.Click += (_, _) => FinishWithResult(null);
        root.AddView(close, closeParams);

        var torch = CreateTopButton(IsChinese ? "手电筒" : "Torch", Dp);
        var torchParams = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(40), GravityFlags.Top | GravityFlags.End)
        {
            RightMargin = Dp(16),
            TopMargin = Dp(44),
        };
        torch.Visibility = ViewStates.Gone;
        torch.Click += (_, _) => ToggleTorch();
        root.AddView(torch, torchParams);
        _torchButton = torch;

        root.LayoutChange += (_, _) => UpdateSurfaceAspectLayout();

        SetContentView(root);
    }

    private Button CreateTopButton(string text, Func<int, int> dp)
    {
        var button = new Button(this)
        {
            Text = text,
            TextSize = 14,
        };
        button.SetAllCaps(false);
        button.SetTextColor(Color.White);
        button.SetPadding(dp(16), 0, dp(16), 0);
        var bg = new global::Android.Graphics.Drawables.GradientDrawable();
        bg.SetColor(Color.Argb(140, 0, 0, 0));
        bg.SetCornerRadius(dp(20));
        button.Background = bg;
        return button;
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (Volatile.Read(ref _finished) != 0) return;
        StartCameraThread();
        if (_surfaceReady)
        {
            EnsurePermissionAndOpenCamera();
        }
    }

    protected override void OnPause()
    {
        Interlocked.Increment(ref _openGeneration);
        CloseCameraAndStopThreadAsync();
        base.OnPause();
    }

    protected override void OnStop()
    {
        base.OnStop();
        // If the user switched away from the app while the scanner was open (rather than finishing back to MainActivity),
        // propagate the true background event to MobileHost.
        if (!IsFinishing)
        {
            MobileHost.NotifyBackgrounded();
        }
    }

    protected override void OnDestroy()
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0)
        {
            AndroidQrScanner.Complete(null);
        }
        AndroidQrScanner.MarkClosed();
        base.OnDestroy();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != RequestCameraCode) return;
        _permissionRequested = false;
        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
        {
            StartCameraThread();
            if (_surfaceReady)
            {
                ScheduleOpenCamera();
            }
        }
        else
        {
            Toast.MakeText(
                this,
                IsChinese ? "需要相机权限才能扫描二维码" : "Camera permission is required to scan QR codes",
                ToastLength.Short)?.Show();
            FinishWithResult(null);
        }
    }

    public void SurfaceCreated(ISurfaceHolder holder)
    {
        _surfaceReady = true;
        UpdateSurfaceAspectLayout();
        EnsurePermissionAndOpenCamera();
    }

    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
    {
        _surfaceReady = true;
    }

    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        _surfaceReady = false;
        Interlocked.Increment(ref _openGeneration);
        CloseCameraAsync();
    }

    private void EnsurePermissionAndOpenCamera()
    {
        if (Volatile.Read(ref _finished) != 0 || IsFinishing || IsDestroyed) return;
        if (CheckSelfPermission(Manifest.Permission.Camera) == Permission.Granted)
        {
            StartCameraThread();
            ScheduleOpenCamera();
        }
        else if (!_permissionRequested)
        {
            _permissionRequested = true;
            RequestPermissions([Manifest.Permission.Camera], RequestCameraCode);
        }
    }

    private void StartCameraThread()
    {
        lock (_cameraLock)
        {
            if (_cameraThread is { IsAlive: true } && _cameraHandler is not null) return;
            _cameraThread = new HandlerThread("octopus-qr-camera");
            _cameraThread.Start();
            if (_cameraThread.Looper is { } looper) _cameraHandler = new Handler(looper);
        }
    }

    /// <summary>
    /// Posts all Camera2 discovery, characteristic queries, ImageReader creation, and OpenCamera Binder IPC
    /// onto <see cref="_cameraHandler"/> so opening the scanner never stalls the Android UI thread.
    /// </summary>
    private void ScheduleOpenCamera()
    {
        Handler? cameraHandler;
        int generation;
        lock (_cameraLock)
        {
            if (_cameraDevice is not null || _cameraOpening || Volatile.Read(ref _finished) != 0 || IsFinishing || IsDestroyed) return;
            cameraHandler = _cameraHandler;
            if (cameraHandler is null) return;
            _cameraOpening = true;
            generation = Volatile.Read(ref _openGeneration);
        }

        if (!cameraHandler.Post(() => OpenCameraOnBackground(generation, cameraHandler)))
        {
            lock (_cameraLock) _cameraOpening = false;
        }
    }

    private void OpenCameraOnBackground(int generation, Handler cameraHandler)
    {
        var gateHeld = false;
        try
        {
            // Wait for any previous asynchronous camera close to finish cleanly so Camera2 never fails with CAMERA_IN_USE.
            CameraHardwareGate.Wait();
            gateHeld = true;

            if (generation != Volatile.Read(ref _openGeneration) || Volatile.Read(ref _finished) != 0 || IsFinishing || IsDestroyed)
            {
                lock (_cameraLock) _cameraOpening = false;
                return;
            }
            if (GetSystemService(CameraService) is not CameraManager manager)
            {
                lock (_cameraLock) _cameraOpening = false;
                FinishWithResult(null);
                return;
            }

            var profile = s_cachedProfile ?? DiscoverCameraProfile(manager);
            if (profile is null)
            {
                lock (_cameraLock) _cameraOpening = false;
                FinishWithResult(null);
                return;
            }
            s_cachedProfile = profile;
            _activeProfile = profile;

            _hasFlash = profile.HasFlash;
            _previewWidth = profile.PreviewWidth;
            _previewHeight = profile.PreviewHeight;

            RunOnUiThread(() =>
            {
                if (IsFinishing || IsDestroyed) return;
                _torchButton?.Visibility = _hasFlash ? ViewStates.Visible : ViewStates.Gone;
                _surfaceView?.Holder?.SetFixedSize(_previewWidth, _previewHeight);
                UpdateSurfaceAspectLayout();
            });

            DeviceStateCallback callback;
            lock (_cameraLock)
            {
                if (generation != Volatile.Read(ref _openGeneration) || Volatile.Read(ref _finished) != 0 || _cameraHandler is null)
                {
                    _cameraOpening = false;
                    return;
                }
                try { _imageReader?.Close(); } catch { }
                _imageReader = ImageReader.NewInstance(profile.AnalysisWidth, profile.AnalysisHeight, ImageFormatType.Yuv420888, maxImages: 2);
                _imageReader.SetOnImageAvailableListener(this, cameraHandler);
                callback = _deviceCallback = new DeviceStateCallback(this, generation);
            }

            manager.OpenCamera(profile.CameraId, callback, cameraHandler);
        }
        catch
        {
            s_cachedProfile = null;
            lock (_cameraLock) _cameraOpening = false;
            FinishWithResult(null);
        }
        finally
        {
            if (gateHeld) CameraHardwareGate.Release();
        }
    }

    private static CameraProfile? DiscoverCameraProfile(CameraManager manager)
    {
        string? selectedId = null;
        CameraCharacteristics? selectedChars = null;
        foreach (var id in manager.GetCameraIdList())
        {
            var chars = manager.GetCameraCharacteristics(id);
            var facing = (Java.Lang.Integer?)chars.Get(CameraCharacteristics.LensFacing);
            if (facing?.IntValue() == (int)LensFacing.Back)
            {
                selectedId = id;
                selectedChars = chars;
                break;
            }
            if (selectedId is null)
            {
                selectedId = id;
                selectedChars = chars;
            }
        }

        if (selectedId is null || selectedChars is null) return null;

        var hasFlash = ((Java.Lang.Boolean?)selectedChars.Get(CameraCharacteristics.FlashInfoAvailable))?.BooleanValue() == true;
        var supportsContinuousAf = SupportsAfMode(selectedChars, (int)ControlAFMode.ContinuousPicture);
        var supportsAutoAf = SupportsAfMode(selectedChars, (int)ControlAFMode.Auto);
        var maxAfRegions = ((Java.Lang.Integer?)selectedChars.Get(CameraCharacteristics.ControlMaxRegionsAf))?.IntValue() ?? 0;

        var sensorW = 0;
        var sensorH = 0;
        if (selectedChars.Get(CameraCharacteristics.SensorInfoActiveArraySize) is Rect activeRect)
        {
            sensorW = activeRect.Width();
            sensorH = activeRect.Height();
        }

        var (fpsMin, fpsMax) = ChooseTargetFpsRange(selectedChars);

        var previewSize = new Size(1280, 720);
        var analysisSize = new Size(800, 600);

        if (selectedChars.Get(CameraCharacteristics.ScalerStreamConfigurationMap) is StreamConfigurationMap map)
        {
            var yuvSizes = map.GetOutputSizes((int)ImageFormatType.Yuv420888);
            Size[]? surfaceSizes = null;
            try
            {
                var surfaceHolderClass = Java.Lang.Class.FromType(typeof(ISurfaceHolder));
                if (surfaceHolderClass is not null)
                {
                    surfaceSizes = map.GetOutputSizes(surfaceHolderClass);
                }
            }
            catch { }

            if (surfaceSizes is { Length: > 0 })
            {
                previewSize = ChooseTargetSize(surfaceSizes, targetW: 1280, targetH: 720, minW: 640, minH: 480, maxW: 1920, maxH: 1440);
            }
            else if (yuvSizes is { Length: > 0 })
            {
                previewSize = ChooseTargetSize(yuvSizes, targetW: 1280, targetH: 720, minW: 640, minH: 480, maxW: 1920, maxH: 1440);
            }

            if (yuvSizes is { Length: > 0 })
            {
                analysisSize = ChooseTargetSize(yuvSizes, targetW: 800, targetH: 600, minW: 480, minH: 360, maxW: 1280, maxH: 960);
            }
            else
            {
                analysisSize = previewSize;
            }
        }

        return new CameraProfile(
            selectedId,
            hasFlash,
            supportsContinuousAf,
            supportsAutoAf,
            maxAfRegions,
            sensorW,
            sensorH,
            previewSize.Width,
            previewSize.Height,
            analysisSize.Width,
            analysisSize.Height,
            fpsMin,
            fpsMax);
    }

    private static (int Min, int Max) ChooseTargetFpsRange(CameraCharacteristics chars)
    {
        try
        {
            if (chars.Get(CameraCharacteristics.ControlAeAvailableTargetFpsRanges)?.ToArray<global::Android.Util.Range>() is not { Length: > 0 } ranges)
            {
                return (0, 0);
            }
            var bestMin = 0;
            var bestMax = 0;
            var bestScore = int.MinValue;
            foreach (var r in ranges)
            {
                var lo = ((Java.Lang.Integer?)r.Lower)?.IntValue() ?? 0;
                var hi = ((Java.Lang.Integer?)r.Upper)?.IntValue() ?? 0;
                if (hi <= 0 || hi > 30) continue; // Keep <= 30fps to preserve exposure brightness indoors.
                // Prefer high upper bound (30fps) and higher lower bound (>=24fps) to avoid 15fps indoor preview stutter.
                var score = hi * 100 + lo * 2;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMin = lo;
                    bestMax = hi;
                }
            }
            return (bestMin, bestMax);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static bool SupportsAfMode(CameraCharacteristics chars, int desiredMode)
    {
        try
        {
            if (chars.Get(CameraCharacteristics.ControlAfAvailableModes)?.ToArray<int>() is { } modes)
            {
                return Array.IndexOf(modes, desiredMode) >= 0;
            }
        }
        catch { }
        return false;
    }

    private static Size ChooseTargetSize(Size[] candidates, int targetW, int targetH, int minW, int minH, int maxW, int maxH)
    {
        Size? best = null;
        var bestScore = int.MaxValue;
        foreach (var size in candidates)
        {
            if (size.Width < minW || size.Height < minH || size.Width > maxW || size.Height > maxH) continue;
            var score = Math.Abs(size.Width - targetW) + Math.Abs(size.Height - targetH);
            if (score < bestScore)
            {
                bestScore = score;
                best = size;
            }
        }
        if (best is not null) return best;

        Size? smallestUsable = null;
        foreach (var size in candidates)
        {
            if (size.Width < 320 || size.Height < 240) continue;
            if (smallestUsable is null || (long)size.Width * size.Height < (long)smallestUsable.Width * smallestUsable.Height)
            {
                smallestUsable = size;
            }
        }
        return smallestUsable ?? candidates[0];
    }

    /// <summary>
    /// Sizes the centered <see cref="SurfaceView"/> to center-crop fill the portrait display without stretching.
    /// </summary>
    private void UpdateSurfaceAspectLayout()
    {
        if (_root is null || _surfaceView is null) return;
        var containerW = _root.Width;
        var containerH = _root.Height;
        if (containerW <= 0 || containerH <= 0 || _previewWidth <= 0 || _previewHeight <= 0) return;

        // Portrait sensor preview swaps width/height relative to display.
        var bufferW = (float)_previewHeight;
        var bufferH = (float)_previewWidth;
        var scale = Math.Max(containerW / bufferW, containerH / bufferH);
        var targetW = (int)Math.Ceiling(bufferW * scale);
        var targetH = (int)Math.Ceiling(bufferH * scale);

        if (_surfaceView.LayoutParameters is FrameLayout.LayoutParams lp
            && (lp.Width != targetW || lp.Height != targetH))
        {
            lp.Width = targetW;
            lp.Height = targetH;
            lp.Gravity = GravityFlags.Center;
            _surfaceView.LayoutParameters = lp;
        }
    }

    private void CreatePreviewSession(int generation)
    {
        CameraDevice? camera;
        ImageReader? reader;
        Handler? cameraHandler;
        CameraProfile? profile;
        lock (_cameraLock)
        {
            camera = _cameraDevice;
            reader = _imageReader;
            cameraHandler = _cameraHandler;
            profile = _activeProfile;
        }
        if (generation != Volatile.Read(ref _openGeneration) || camera is null || reader is null || Volatile.Read(ref _finished) != 0 || IsFinishing || IsDestroyed) return;
        var previewSurface = _surfaceView?.Holder?.Surface;
        if (previewSurface is not { IsValid: true } || reader.Surface is not { IsValid: true } readerSurface) return;

        try
        {
            var builder = camera.CreateCaptureRequest(CameraTemplate.Preview);
            builder.AddTarget(previewSurface);
            builder.AddTarget(readerSurface);
            if (CaptureRequest.ControlMode is not null)
            {
                builder.Set(CaptureRequest.ControlMode, (int)ControlMode.Auto);
            }
            if (profile?.SupportsContinuousAf == true && CaptureRequest.ControlAfMode is not null)
            {
                builder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.ContinuousPicture);
            }
            if (CaptureRequest.ControlAeMode is not null)
            {
                builder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.On);
            }
            if (profile is { FpsMin: > 0, FpsMax: > 0 } && CaptureRequest.ControlAeTargetFpsRange is not null)
            {
                builder.Set(CaptureRequest.ControlAeTargetFpsRange, new global::Android.Util.Range(profile.FpsMin, profile.FpsMax));
            }

            var sessionCallback = new SessionStateCallback(this, generation);
            lock (_cameraLock)
            {
                if (generation != Volatile.Read(ref _openGeneration) || _cameraDevice != camera || Volatile.Read(ref _finished) != 0)
                {
                    return;
                }
                _previewBuilder = builder;
                _sessionCallback = sessionCallback;
            }

            if (OperatingSystem.IsAndroidVersionAtLeast(28) && MainExecutor is { } executor)
            {
                var outputs = new List<OutputConfiguration>
                {
                    new(previewSurface),
                    new(readerSurface),
                };
                var config = new SessionConfiguration((int)SessionType.Regular, outputs, executor, sessionCallback);
                camera.CreateCaptureSession(config);
            }
            else
            {
#pragma warning disable CA1422 // Fallback for Android 7–8 (API 24–27); API 28+ uses SessionConfiguration above.
                camera.CreateCaptureSession([previewSurface, readerSurface], sessionCallback, cameraHandler);
#pragma warning restore CA1422
            }
        }
        catch
        {
            FinishWithResult(null);
        }
    }

    private void OnSurfaceTouch(object? sender, View.TouchEventArgs e)
    {
        if (e.Event is null || e.Event.ActionMasked != MotionEventActions.Down)
        {
            e.Handled = false;
            return;
        }
        e.Handled = true;
        var tapX = e.Event.RawX;
        var tapY = e.Event.RawY;
        _overlay?.ShowFocusRing(tapX, tapY);
        TriggerTapFocus(e.Event.GetX(), e.Event.GetY());
    }

    private void TriggerTapFocus(float viewX, float viewY)
    {
        CameraCaptureSession? session;
        CaptureRequest.Builder? builder;
        Handler? handler;
        CameraProfile? profile;
        lock (_cameraLock)
        {
            session = _captureSession;
            builder = _previewBuilder;
            handler = _cameraHandler;
            profile = _activeProfile;
        }
        if (session is null || builder is null || handler is null || profile is null || _surfaceView is null) return;
        if (profile.MaxAfRegions <= 0 || profile.SensorWidth <= 0 || profile.SensorHeight <= 0) return;

        var svW = Math.Max(1, _surfaceView.Width);
        var svH = Math.Max(1, _surfaceView.Height);
        // Portrait 90° CW sensor mapping: screen Y maps along sensor X, screen X maps inverted along sensor Y.
        var nx = Math.Clamp(viewY / svH, 0f, 1f);
        var ny = Math.Clamp(1f - viewX / svW, 0f, 1f);
        var cx = (int)(nx * profile.SensorWidth);
        var cy = (int)(ny * profile.SensorHeight);
        var half = Math.Max(64, Math.Min(profile.SensorWidth, profile.SensorHeight) / 10);
        var rect = new Rect(
            Math.Max(0, cx - half),
            Math.Max(0, cy - half),
            Math.Min(profile.SensorWidth, cx + half),
            Math.Min(profile.SensorHeight, cy + half));

        handler.Post(() =>
        {
            try
            {
                if (Volatile.Read(ref _finished) != 0) return;
                var regions = new[] { new MeteringRectangle(rect, MeteringRectangle.MeteringWeightMax) };
                if (CaptureRequest.ControlAfRegions is not null) builder.Set(CaptureRequest.ControlAfRegions, regions);
                if (profile.SupportsAutoAf && CaptureRequest.ControlAfMode is not null)
                {
                    builder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.Auto);
                }
                if (CaptureRequest.ControlAfTrigger is not null)
                {
                    builder.Set(CaptureRequest.ControlAfTrigger, (int)ControlAFTrigger.Start);
                    session.Capture(builder.Build(), null, handler);
                    builder.Set(CaptureRequest.ControlAfTrigger, (int)ControlAFTrigger.Idle);
                }
                if (profile.SupportsContinuousAf && CaptureRequest.ControlAfMode is not null)
                {
                    builder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.ContinuousPicture);
                }
                session.SetRepeatingRequest(builder.Build(), null, handler);
            }
            catch { }
        });
    }

    private void ToggleTorch()
    {
        if (!_hasFlash || _captureSession is null || _previewBuilder is null) return;
        _torchOn = !_torchOn;
        try
        {
            if (CaptureRequest.FlashMode is not null)
            {
                _previewBuilder.Set(CaptureRequest.FlashMode, (int)(_torchOn ? FlashMode.Torch : FlashMode.Off));
            }
            _captureSession.SetRepeatingRequest(_previewBuilder.Build(), null, _cameraHandler);
            _torchButton?.Text = _torchOn
                ? (IsChinese ? "关闭照明" : "Torch On")
                : (IsChinese ? "手电筒" : "Torch");
        }
        catch { }
    }

    public void OnImageAvailable(ImageReader? reader)
    {
        // Runs on the camera HandlerThread: keep this ultra-fast (<0.5ms) and NEVER run ZXing decoding here.
        try
        {
            if (reader is null || Volatile.Read(ref _finished) != 0) return;

            byte[]? decodeBytes = null;
            var cropW = 0;
            var cropH = 0;
            var acquired = false;

            using (var image = reader.AcquireLatestImage())
            {
                if (image is null || Volatile.Read(ref _finished) != 0) return;

                var now = SystemClock.ElapsedRealtime();
                if (now - _lastDecodeMs < DecodeIntervalMs) return;

                // Drop frames while a previous decode is still running; image closes immediately on return.
                if (Interlocked.CompareExchange(ref _decoding, 1, 0) != 0) return;
                acquired = true;
                _lastDecodeMs = now;

                var planes = image.GetPlanes();
                if (planes is null || planes.Length == 0)
                {
                    Volatile.Write(ref _decoding, 0);
                    return;
                }

                var yPlane = planes[0];
                var buffer = yPlane.Buffer;
                if (buffer is null)
                {
                    Volatile.Write(ref _decoding, 0);
                    return;
                }

                var width = image.Width;
                var height = image.Height;
                var rowStride = yPlane.RowStride;
                var pixelStride = Math.Max(1, yPlane.PixelStride);
                if (width <= 0 || height <= 0 || rowStride < width)
                {
                    Volatile.Write(ref _decoding, 0);
                    return;
                }

                var side = Math.Max(160, (int)(Math.Min(width, height) * RoiFraction));
                cropW = Math.Min(side, width);
                cropH = Math.Min(side, height);
                var cropX = (width - cropW) / 2;
                var cropY = (height - cropH) / 2;

                var roiLength = cropW * cropH;
                if (_roiBuffer is null || _roiBuffer.Length != roiLength) _roiBuffer = new byte[roiLength];

                if (!CopyLuminanceRoi(buffer, rowStride, pixelStride, cropX, cropY, cropW, cropH, _roiBuffer)
                    || !HasUsableContrast(_roiBuffer, cropW, cropH))
                {
                    Volatile.Write(ref _decoding, 0);
                    return;
                }

                decodeBytes = _roiBuffer;
            }

            // Image is already closed here: Camera2 preview pipeline is never stalled while ZXing runs.
            if (decodeBytes is null)
            {
                if (acquired) Volatile.Write(ref _decoding, 0);
                return;
            }

            var attempt = Interlocked.Increment(ref _decodeCounter);
            var useHarder = (attempt & 3) == 0;
            _ = Task.Run(() => DecodeRoiWorker(decodeBytes, cropW, cropH, useHarder));
        }
        catch
        {
            Volatile.Write(ref _decoding, 0);
        }
    }

    /// <summary>
    /// Samples a 5×5 grid across the ROI to skip pitch-black / unconverged AE startup frames or covered lenses
    /// without invoking ZXing.
    /// </summary>
    private static bool HasUsableContrast(byte[] luminance, int width, int height)
    {
        var min = 255;
        var max = 0;
        var stepX = Math.Max(1, width / 6);
        var stepY = Math.Max(1, height / 6);
        for (var y = stepY; y < height; y += stepY)
        {
            var row = y * width;
            for (var x = stepX; x < width; x += stepX)
            {
                var v = luminance[row + x];
                if (v < min) min = v;
                if (v > max) max = v;
                if (max - min >= MinLumaContrast) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Extracts the cropped Y-plane luminance ROI directly from the native DirectByteBuffer address via unsafe
    /// <c>Buffer.MemoryCopy</c> with zero JNI array allocations and zero per-row marshalling overhead.
    /// </summary>
    private unsafe bool CopyLuminanceRoi(
        Java.Nio.ByteBuffer buffer,
        int rowStride,
        int pixelStride,
        int cropX,
        int cropY,
        int cropW,
        int cropH,
        byte[] dst)
    {
        var limit = buffer.Limit();
        if (limit <= 0) return false;

        var directPtr = buffer.IsDirect ? JNIEnv.GetDirectBufferAddress(buffer.Handle) : IntPtr.Zero;
        if (directPtr != IntPtr.Zero)
        {
            var capacity = JNIEnv.GetDirectBufferCapacity(buffer.Handle);
            var available = capacity > 0 ? (int)Math.Min(limit, capacity) : limit;
            var srcBase = (byte*)directPtr;
            fixed (byte* dstBase = dst)
            {
                if (pixelStride == 1)
                {
                    for (var row = 0; row < cropH; row++)
                    {
                        var srcOffset = (cropY + row) * rowStride + cropX;
                        if (srcOffset < 0 || srcOffset >= available) break;
                        var count = Math.Min(cropW, available - srcOffset);
                        Buffer.MemoryCopy(srcBase + srcOffset, dstBase + row * cropW, count, count);
                    }
                    return true;
                }

                for (var row = 0; row < cropH; row++)
                {
                    var rowBase = (cropY + row) * rowStride + cropX * pixelStride;
                    var dstRow = dstBase + row * cropW;
                    for (var col = 0; col < cropW; col++)
                    {
                        var idx = rowBase + col * pixelStride;
                        dstRow[col] = idx >= 0 && idx < available ? srcBase[idx] : (byte)0;
                    }
                }
            }
            return true;
        }

        // Non-direct ByteBuffer fallback: perform ONE bulk copy into a reusable flat buffer instead of per-row JNI calls.
        if (_fallbackFlatBuffer is null || _fallbackFlatBuffer.Length < limit) _fallbackFlatBuffer = new byte[limit];
        buffer.Rewind();
        buffer.Get(_fallbackFlatBuffer, 0, limit);

        if (pixelStride == 1)
        {
            for (var row = 0; row < cropH; row++)
            {
                var srcOffset = (cropY + row) * rowStride + cropX;
                if (srcOffset < 0 || srcOffset >= limit) break;
                var count = Math.Min(cropW, limit - srcOffset);
                Buffer.BlockCopy(_fallbackFlatBuffer, srcOffset, dst, row * cropW, count);
            }
        }
        else
        {
            for (var row = 0; row < cropH; row++)
            {
                var rowBase = (cropY + row) * rowStride + cropX * pixelStride;
                var dstRow = row * cropW;
                for (var col = 0; col < cropW; col++)
                {
                    var idx = rowBase + col * pixelStride;
                    dst[dstRow + col] = idx >= 0 && idx < limit ? _fallbackFlatBuffer[idx] : (byte)0;
                }
            }
        }
        return true;
    }

    private void DecodeRoiWorker(byte[] luminance, int width, int height, bool useHarder)
    {
        try
        {
            if (Volatile.Read(ref _finished) != 0) return;
            var decoded = TryDecode(luminance, width, height, useHarder);
            if (!string.IsNullOrEmpty(decoded) && Volatile.Read(ref _finished) == 0)
            {
                RunOnUiThread(() =>
                {
                    try
                    {
                        var haptic = OperatingSystem.IsAndroidVersionAtLeast(30) ? FeedbackConstants.Confirm : FeedbackConstants.VirtualKey;
                        _surfaceView?.PerformHapticFeedback(haptic);
                    }
                    catch { }
                    FinishWithResult(decoded);
                });
            }
        }
        finally
        {
            Volatile.Write(ref _decoding, 0);
        }
    }

    private string? TryDecode(byte[] luminance, int width, int height, bool useHarder)
    {
        // Lazily initialize QRCodeReader off the UI thread so OnCreate stays instantaneous.
        var reader = _reader ??= new QRCodeReader();
        try
        {
            var source = new PlanarYUVLuminanceSource(luminance, width, height, 0, 0, width, height, false);
            var hints = useHarder ? _harderHints : _fastHints;
            var result = reader.decode(new BinaryBitmap(new HybridBinarizer(source)), hints);
            if (result is not null) return result.Text;

            if (useHarder)
            {
                reader.reset();
                result = reader.decode(new BinaryBitmap(new GlobalHistogramBinarizer(source)), _fastHints);
                return result?.Text;
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { reader.reset(); } catch { }
        }
    }

    private void CloseCameraAsync()
    {
        var (session, device, reader) = DetachCameraResources();
        if (session is null && device is null && reader is null) return;
        _ = Task.Run(() => ReleaseCameraResourcesSerialized(session, device, reader, threadToStop: null));
    }

    private void CloseCameraAndStopThreadAsync()
    {
        var (session, device, reader) = DetachCameraResources();
        HandlerThread? thread;
        lock (_cameraLock)
        {
            thread = _cameraThread;
            _cameraThread = null;
            _cameraHandler = null;
        }
        if (session is null && device is null && reader is null && thread is null) return;
        _ = Task.Run(() => ReleaseCameraResourcesSerialized(session, device, reader, thread));
    }

    private (CameraCaptureSession? Session, CameraDevice? Device, ImageReader? Reader) DetachCameraResources()
    {
        lock (_cameraLock)
        {
            _cameraOpening = false;
            var session = _captureSession;
            _captureSession = null;
            var device = _cameraDevice;
            _cameraDevice = null;
            var reader = _imageReader;
            _imageReader = null;
            _previewBuilder = null;
            _deviceCallback = null;
            _sessionCallback = null;
            return (session, device, reader);
        }
    }

    private static void ReleaseCameraResourcesSerialized(
        CameraCaptureSession? session,
        CameraDevice? device,
        ImageReader? reader,
        HandlerThread? threadToStop)
    {
        CameraHardwareGate.Wait();
        try
        {
            // Camera2 HAL close calls can block 80-300ms on multi-camera OEM HALs; run off the UI thread.
            try { session?.Close(); } catch { }
            try { device?.Close(); } catch { }
            try { reader?.Close(); } catch { }
            if (threadToStop is not null)
            {
                try
                {
                    threadToStop.QuitSafely();
                    threadToStop.Join(150);
                }
                catch { }
            }
        }
        finally
        {
            CameraHardwareGate.Release();
        }
    }

    private void FinishWithResult(string? value)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        Interlocked.Increment(ref _openGeneration);
        // Complete and dismiss the activity immediately (0ms UI exit latency); camera HAL tears down asynchronously.
        AndroidQrScanner.Complete(value);
        CloseCameraAndStopThreadAsync();
        RunOnUiThread(() =>
        {
            if (!IsFinishing && !IsDestroyed) Finish();
        });
    }

    private sealed class DeviceStateCallback(QrScannerActivity owner, int generation) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera)
        {
            lock (owner._cameraLock)
            {
                owner._cameraOpening = false;
                if (generation != Volatile.Read(ref owner._openGeneration)
                    || Volatile.Read(ref owner._finished) != 0
                    || owner.IsFinishing
                    || owner.IsDestroyed)
                {
                    _ = Task.Run(() => ReleaseCameraResourcesSerialized(null, camera, null, null));
                    return;
                }
                owner._cameraDevice = camera;
            }
            owner.CreatePreviewSession(generation);
        }

        public override void OnDisconnected(CameraDevice camera)
        {
            lock (owner._cameraLock)
            {
                owner._cameraOpening = false;
                if (ReferenceEquals(owner._cameraDevice, camera)) owner._cameraDevice = null;
            }
            _ = Task.Run(() => ReleaseCameraResourcesSerialized(null, camera, null, null));
            owner.FinishWithResult(null);
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            lock (owner._cameraLock)
            {
                owner._cameraOpening = false;
                if (ReferenceEquals(owner._cameraDevice, camera)) owner._cameraDevice = null;
            }
            _ = Task.Run(() => ReleaseCameraResourcesSerialized(null, camera, null, null));
            owner.FinishWithResult(null);
        }
    }

    private sealed class SessionStateCallback(QrScannerActivity owner, int generation) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session)
        {
            CaptureRequest.Builder? builder;
            Handler? handler;
            lock (owner._cameraLock)
            {
                if (generation != Volatile.Read(ref owner._openGeneration)
                    || owner._cameraDevice is null
                    || owner._previewBuilder is null
                    || Volatile.Read(ref owner._finished) != 0)
                {
                    _ = Task.Run(() => ReleaseCameraResourcesSerialized(session, null, null, null));
                    return;
                }
                owner._captureSession = session;
                builder = owner._previewBuilder;
                handler = owner._cameraHandler;
            }
            try
            {
                session.SetRepeatingRequest(builder.Build(), null, handler);
            }
            catch
            {
                owner.FinishWithResult(null);
            }
        }

        public override void OnConfigureFailed(CameraCaptureSession session) => owner.FinishWithResult(null);
    }

    private sealed class ViewfinderOverlayView : View
    {
        private readonly string _hint;
        private readonly Paint _scrimPaint;
        private readonly Paint _cornerPaint;
        private readonly Paint _focusPaint;
        private readonly Paint _textPaint;
        private readonly global::Android.Graphics.Path _scrimPath = new();
        private readonly RectF _cutoutRect = new();
        private readonly float _density;
        private float _focusX = -1f;
        private float _focusY = -1f;
        private long _focusUntilMs;

        public ViewfinderOverlayView(Context context, string hint) : base(context)
        {
            _hint = hint;
            _density = context.Resources?.DisplayMetrics?.Density ?? 2f;
            _scrimPaint = new Paint(PaintFlags.AntiAlias) { Color = Color.Argb(145, 0, 0, 0) };
            _scrimPaint.SetStyle(Paint.Style.Fill);
            _cornerPaint = new Paint(PaintFlags.AntiAlias)
            {
                Color = Color.White,
                StrokeWidth = 3.5f * _density,
                StrokeCap = Paint.Cap.Round,
                StrokeJoin = Paint.Join.Round,
            };
            _cornerPaint.SetStyle(Paint.Style.Stroke);
            _focusPaint = new Paint(PaintFlags.AntiAlias)
            {
                Color = Color.Argb(220, 255, 255, 255),
                StrokeWidth = 2f * _density,
            };
            _focusPaint.SetStyle(Paint.Style.Stroke);
            _textPaint = new Paint(PaintFlags.AntiAlias)
            {
                Color = Color.White,
                TextSize = 15f * _density,
                TextAlign = Paint.Align.Center,
            };
        }

        public void ShowFocusRing(float x, float y)
        {
            _focusX = x;
            _focusY = y;
            _focusUntilMs = SystemClock.ElapsedRealtime() + 650;
            Invalidate();
            PostDelayed(Invalidate, 680);
        }

        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            var w = Width;
            var h = Height;
            if (w <= 0 || h <= 0) return;

            var side = Math.Min(w * 0.70f, 280f * _density);
            var left = (w - side) / 2f;
            var top = (h - side) / 2f - 24f * _density;
            var right = left + side;
            var bottom = top + side;
            var radius = 16f * _density;

            _cutoutRect.Set(left, top, right, bottom);
            _scrimPath.Reset();
            _scrimPath.SetFillType(global::Android.Graphics.Path.FillType.EvenOdd!);
            _scrimPath.AddRect(0, 0, w, h, global::Android.Graphics.Path.Direction.Cw!);
            _scrimPath.AddRoundRect(_cutoutRect, radius, radius, global::Android.Graphics.Path.Direction.Cw!);
            canvas.DrawPath(_scrimPath, _scrimPaint);

            var arm = 24f * _density;
            // Top-left
            canvas.DrawLine(left, top + arm, left, top, _cornerPaint);
            canvas.DrawLine(left, top, left + arm, top, _cornerPaint);
            // Top-right
            canvas.DrawLine(right - arm, top, right, top, _cornerPaint);
            canvas.DrawLine(right, top, right, top + arm, _cornerPaint);
            // Bottom-left
            canvas.DrawLine(left, bottom - arm, left, bottom, _cornerPaint);
            canvas.DrawLine(left, bottom, left + arm, bottom, _cornerPaint);
            // Bottom-right
            canvas.DrawLine(right - arm, bottom, right, bottom, _cornerPaint);
            canvas.DrawLine(right, bottom, right, bottom - arm, _cornerPaint);

            if (_focusX >= 0 && SystemClock.ElapsedRealtime() < _focusUntilMs)
            {
                canvas.DrawCircle(_focusX, _focusY, 26f * _density, _focusPaint);
            }

            canvas.DrawText(_hint, w / 2f, bottom + 36f * _density, _textPaint);
        }
    }
}
