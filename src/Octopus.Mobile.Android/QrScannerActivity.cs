using Android;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
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
        try
        {
            host.StartActivity(intent);
        }
        catch
        {
            Complete(null);
        }
        return tcs.Task;
    }

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
/// Full-screen native Android Camera2 QR scanner with viewfinder overlay, torch toggle, runtime camera permission
/// request, and throttled YUV_420_888 luminance decoding via ZXing.Net.
/// </summary>
[Activity(
    Label = "Scan QR Code",
    Theme = "@android:style/Theme.Black.NoTitleBar.Fullscreen",
    ScreenOrientation = ScreenOrientation.Portrait,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class QrScannerActivity : Activity, TextureView.ISurfaceTextureListener, ImageReader.IOnImageAvailableListener
{
    private const int RequestCameraCode = 47601;
    private const long DecodeIntervalMs = 120;

    private readonly object _cameraLock = new();
    private readonly QRCodeReader _reader = new();
    private readonly Dictionary<DecodeHintType, object> _hints = new()
    {
        [DecodeHintType.TRY_HARDER] = true,
        [DecodeHintType.CHARACTER_SET] = "UTF-8",
        [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
    };

    private TextureView? _textureView;
    private Button? _torchButton;
    private HandlerThread? _cameraThread;
    private Handler? _cameraHandler;
    private CameraDevice? _cameraDevice;
    private CameraCaptureSession? _captureSession;
    private CaptureRequest.Builder? _previewBuilder;
    private ImageReader? _imageReader;
    private Surface? _previewSurface;
    private DeviceStateCallback? _deviceCallback;
    private SessionStateCallback? _sessionCallback;
    private string? _cameraId;
    private bool _cameraOpening;
    private bool _permissionRequested;
    private bool _supportsContinuousAf;
    private Size _previewSize = new(1280, 720);
    private bool _hasFlash;
    private bool _torchOn;
    private long _lastDecodeMs;
    private int _frameCounter;
    private byte[]? _yBuffer;
    private byte[]? _rotatedBuffer;
    private int _finished;

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

        var texture = new TextureView(this)
        {
            LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent),
        };
        texture.SurfaceTextureListener = this;
        root.AddView(texture);
        _textureView = texture;

        var overlay = new ViewfinderOverlayView(this, IsChinese
            ? "将电脑上的配对二维码放入框内"
            : "Align the computer's pairing QR code inside the frame")
        {
            LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent),
        };
        root.AddView(overlay);

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
        if (_textureView is { IsAvailable: true })
        {
            EnsurePermissionAndOpenCamera();
        }
    }

    protected override void OnPause()
    {
        CloseCamera();
        StopCameraThread();
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        if (Interlocked.Exchange(ref _finished, 1) == 0)
        {
            AndroidQrScanner.Complete(null);
        }
        base.OnDestroy();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != RequestCameraCode) return;
        _permissionRequested = false;
        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
        {
            // OnPause() stopped the camera thread while the system permission dialog was visible.
            // Restart the thread before opening the camera (and guard against double-open when OnResume runs next).
            StartCameraThread();
            if (_textureView is { IsAvailable: true })
            {
                OpenCamera();
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

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height) => EnsurePermissionAndOpenCamera();
    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
    {
        CloseCamera();
        return true;
    }
    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) => ConfigureTransform(width, height);
    public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }

    private void EnsurePermissionAndOpenCamera()
    {
        if (Volatile.Read(ref _finished) != 0 || IsFinishing || IsDestroyed) return;
        if (CheckSelfPermission(Manifest.Permission.Camera) == Permission.Granted)
        {
            StartCameraThread();
            OpenCamera();
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

    private void StopCameraThread()
    {
        HandlerThread? thread;
        lock (_cameraLock)
        {
            thread = _cameraThread;
            _cameraThread = null;
            _cameraHandler = null;
        }
        if (thread is null) return;
        try
        {
            thread.QuitSafely();
            thread.Join(500);
        }
        catch { }
    }

    private void OpenCamera()
    {
        Handler? cameraHandler;
        lock (_cameraLock)
        {
            if (_cameraDevice is not null || _cameraOpening || Volatile.Read(ref _finished) != 0 || IsFinishing || IsDestroyed) return;
            cameraHandler = _cameraHandler;
            if (cameraHandler is null) return;
            _cameraOpening = true;
        }
        if (GetSystemService(CameraService) is not CameraManager manager)
        {
            lock (_cameraLock) _cameraOpening = false;
            FinishWithResult(null);
            return;
        }

        try
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

            if (selectedId is null || selectedChars is null)
            {
                lock (_cameraLock) _cameraOpening = false;
                FinishWithResult(null);
                return;
            }

            _cameraId = selectedId;
            _hasFlash = ((Java.Lang.Boolean?)selectedChars.Get(CameraCharacteristics.FlashInfoAvailable))?.BooleanValue() == true;
            _supportsContinuousAf = SupportsAfMode(selectedChars, (int)ControlAFMode.ContinuousPicture);
            if (_torchButton is not null)
            {
                RunOnUiThread(() => _torchButton.Visibility = _hasFlash ? ViewStates.Visible : ViewStates.Gone);
            }

            if (selectedChars.Get(CameraCharacteristics.ScalerStreamConfigurationMap) is StreamConfigurationMap map)
            {
                var yuvSizes = map.GetOutputSizes((int)ImageFormatType.Yuv420888);
                Size[]? previewSizes = null;
                try
                {
                    var surfaceTextureClass = Java.Lang.Class.FromType(typeof(SurfaceTexture));
                    if (surfaceTextureClass is not null)
                    {
                        previewSizes = map.GetOutputSizes(surfaceTextureClass);
                    }
                }
                catch { }
                if (yuvSizes is { Length: > 0 }) _previewSize = ChooseOptimalSize(yuvSizes, previewSizes);
            }

            lock (_cameraLock)
            {
                if (Volatile.Read(ref _finished) != 0 || _cameraHandler is null)
                {
                    _cameraOpening = false;
                    return;
                }
                try { _imageReader?.Close(); } catch { }
                _imageReader = ImageReader.NewInstance(_previewSize.Width, _previewSize.Height, ImageFormatType.Yuv420888, maxImages: 3);
                _imageReader.SetOnImageAvailableListener(this, cameraHandler);
                _deviceCallback = new DeviceStateCallback(this);
            }

            if (_textureView is not null)
            {
                RunOnUiThread(() => ConfigureTransform(_textureView.Width, _textureView.Height));
            }

            manager.OpenCamera(_cameraId, _deviceCallback, cameraHandler);
        }
        catch
        {
            lock (_cameraLock) _cameraOpening = false;
            FinishWithResult(null);
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

    private static Size ChooseOptimalSize(Size[] yuvSizes, Size[]? previewSizes)
    {
        var candidates = yuvSizes;
        if (previewSizes is { Length: > 0 })
        {
            var previewSet = new HashSet<(int W, int H)>();
            foreach (var s in previewSizes) previewSet.Add((s.Width, s.Height));
            var common = yuvSizes.Where(s => previewSet.Contains((s.Width, s.Height))).ToArray();
            if (common.Length > 0) candidates = common;
        }

        Size? best = null;
        var bestScore = int.MaxValue;
        foreach (var size in candidates)
        {
            if (size.Width < 480 || size.Height < 360 || size.Width > 1920 || size.Height > 1440) continue;
            var score = Math.Abs(size.Width - 1280) + Math.Abs(size.Height - 720);
            if (score < bestScore)
            {
                bestScore = score;
                best = size;
            }
        }
        if (best is not null) return best;

        // Avoid falling back to huge 12MP+ sensor sizes (often sizes[0] on modern phones) that can OOM or stall HAL.
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

    private void ConfigureTransform(int viewWidth, int viewHeight)
    {
        if (_textureView is null || viewWidth <= 0 || viewHeight <= 0) return;
        // Portrait sensor preview swaps width/height relative to display.
        var bufferWidth = (float)_previewSize.Height;
        var bufferHeight = (float)_previewSize.Width;
        var scaleX = viewWidth / bufferWidth;
        var scaleY = viewHeight / bufferHeight;
        var scale = Math.Max(scaleX, scaleY);
        var scaledW = bufferWidth * scale;
        var scaledH = bufferHeight * scale;

        var matrix = new Matrix();
        matrix.SetScale(scaledW / viewWidth, scaledH / viewHeight, viewWidth / 2f, viewHeight / 2f);
        _textureView.SetTransform(matrix);
    }

    private void CreatePreviewSession()
    {
        CameraDevice? camera;
        ImageReader? reader;
        Handler? cameraHandler;
        lock (_cameraLock)
        {
            camera = _cameraDevice;
            reader = _imageReader;
            cameraHandler = _cameraHandler;
        }
        if (camera is null || reader is null || Volatile.Read(ref _finished) != 0 || IsFinishing || IsDestroyed) return;
        if (_textureView?.SurfaceTexture is not { } surfaceTexture || reader.Surface is not { IsValid: true } readerSurface) return;
        try
        {
            surfaceTexture.SetDefaultBufferSize(_previewSize.Width, _previewSize.Height);
            var previewSurface = new Surface(surfaceTexture);
            if (!previewSurface.IsValid)
            {
                previewSurface.Release();
                return;
            }

            var builder = camera.CreateCaptureRequest(CameraTemplate.Preview);
            builder.AddTarget(previewSurface);
            builder.AddTarget(readerSurface);
            if (_supportsContinuousAf && CaptureRequest.ControlAfMode is not null)
            {
                builder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.ContinuousPicture);
            }
            if (CaptureRequest.ControlAeMode is not null)
            {
                builder.Set(CaptureRequest.ControlAeMode, (int)ControlAEMode.On);
            }

            var sessionCallback = new SessionStateCallback(this);
            lock (_cameraLock)
            {
                if (_cameraDevice != camera || Volatile.Read(ref _finished) != 0)
                {
                    previewSurface.Release();
                    return;
                }
                try { _previewSurface?.Release(); } catch { }
                _previewSurface = previewSurface;
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
        // Runs on the camera HandlerThread: an uncaught exception here kills the entire Android process.
        try
        {
            if (reader is null || Volatile.Read(ref _finished) != 0) return;
            using var image = reader.AcquireLatestImage();
            if (image is null || Volatile.Read(ref _finished) != 0) return;

            var now = SystemClock.ElapsedRealtime();
            if (now - _lastDecodeMs < DecodeIntervalMs) return;
            _lastDecodeMs = now;

            var planes = image.GetPlanes();
            if (planes is null || planes.Length == 0) return;
            var yPlane = planes[0];
            var buffer = yPlane.Buffer;
            if (buffer is null) return;

            var width = image.Width;
            var height = image.Height;
            var rowStride = yPlane.RowStride;
            var pixelStride = Math.Max(1, yPlane.PixelStride);
            if (width <= 0 || height <= 0 || rowStride < width) return;

            var total = width * height;
            if (_yBuffer is null || _yBuffer.Length != total) _yBuffer = new byte[total];

            buffer.Rewind();
            var limit = buffer.Limit();
            if (pixelStride == 1 && rowStride == width)
            {
                if (limit < total) return;
                buffer.Get(_yBuffer, 0, total);
            }
            else if (pixelStride == 1)
            {
                for (var row = 0; row < height; row++)
                {
                    var offset = row * rowStride;
                    if (offset >= limit) break;
                    var count = Math.Min(width, limit - offset);
                    buffer.Position(offset);
                    buffer.Get(_yBuffer, row * width, count);
                }
            }
            else
            {
                for (var row = 0; row < height; row++)
                {
                    var rowStart = row * rowStride;
                    for (var col = 0; col < width; col++)
                    {
                        var idx = rowStart + col * pixelStride;
                        _yBuffer[row * width + col] = idx < limit ? (byte)buffer.Get(idx) : (byte)0;
                    }
                }
            }

            // Alternate between native sensor orientation and 90°-rotated frame so portrait and landscape QR codes decode quickly.
            var rotate = (_frameCounter++ & 1) == 1;
            string? decoded;
            if (!rotate)
            {
                decoded = TryDecode(_yBuffer, width, height);
            }
            else
            {
                if (_rotatedBuffer is null || _rotatedBuffer.Length != total) _rotatedBuffer = new byte[total];
                Rotate90Clockwise(_yBuffer, _rotatedBuffer, width, height);
                decoded = TryDecode(_rotatedBuffer, height, width);
            }

            if (!string.IsNullOrEmpty(decoded))
            {
                RunOnUiThread(() =>
                {
                    try
                    {
                        var haptic = OperatingSystem.IsAndroidVersionAtLeast(30) ? FeedbackConstants.Confirm : FeedbackConstants.VirtualKey;
                        _textureView?.PerformHapticFeedback(haptic);
                    }
                    catch { }
                    FinishWithResult(decoded);
                });
            }
        }
        catch
        {
            // Ignore transient Camera2 / HAL / ImageReader / decode errors on individual frames.
        }
    }

    private string? TryDecode(byte[] luminance, int width, int height)
    {
        try
        {
            var source = new PlanarYUVLuminanceSource(luminance, width, height, 0, 0, width, height, false);
            var bitmap = new BinaryBitmap(new HybridBinarizer(source));
            var result = _reader.decode(bitmap, _hints);
            return result?.Text;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { _reader.reset(); } catch { }
        }
    }

    private static void Rotate90Clockwise(byte[] src, byte[] dst, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            var srcRow = y * width;
            var dstCol = height - 1 - y;
            for (var x = 0; x < width; x++)
            {
                dst[x * height + dstCol] = src[srcRow + x];
            }
        }
    }

    private void CloseCamera()
    {
        CameraCaptureSession? session;
        CameraDevice? device;
        ImageReader? reader;
        Surface? previewSurface;
        lock (_cameraLock)
        {
            _cameraOpening = false;
            session = _captureSession;
            _captureSession = null;
            device = _cameraDevice;
            _cameraDevice = null;
            reader = _imageReader;
            _imageReader = null;
            previewSurface = _previewSurface;
            _previewSurface = null;
            _previewBuilder = null;
            _deviceCallback = null;
            _sessionCallback = null;
        }
        try { session?.Close(); } catch { }
        try { device?.Close(); } catch { }
        try { reader?.Close(); } catch { }
        try { previewSurface?.Release(); } catch { }
    }

    private void FinishWithResult(string? value)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0) return;
        CloseCamera();
        AndroidQrScanner.Complete(value);
        RunOnUiThread(() =>
        {
            if (!IsFinishing && !IsDestroyed) Finish();
        });
    }

    private sealed class DeviceStateCallback(QrScannerActivity owner) : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera)
        {
            lock (owner._cameraLock)
            {
                owner._cameraOpening = false;
                if (Volatile.Read(ref owner._finished) != 0 || owner.IsFinishing || owner.IsDestroyed)
                {
                    try { camera.Close(); } catch { }
                    return;
                }
                owner._cameraDevice = camera;
            }
            owner.CreatePreviewSession();
        }

        public override void OnDisconnected(CameraDevice camera)
        {
            lock (owner._cameraLock)
            {
                owner._cameraOpening = false;
                if (ReferenceEquals(owner._cameraDevice, camera)) owner._cameraDevice = null;
            }
            try { camera.Close(); } catch { }
            owner.FinishWithResult(null);
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            lock (owner._cameraLock)
            {
                owner._cameraOpening = false;
                if (ReferenceEquals(owner._cameraDevice, camera)) owner._cameraDevice = null;
            }
            try { camera.Close(); } catch { }
            owner.FinishWithResult(null);
        }
    }

    private sealed class SessionStateCallback(QrScannerActivity owner) : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session)
        {
            CaptureRequest.Builder? builder;
            Handler? handler;
            lock (owner._cameraLock)
            {
                if (owner._cameraDevice is null || owner._previewBuilder is null || Volatile.Read(ref owner._finished) != 0)
                {
                    try { session.Close(); } catch { }
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
        private readonly Paint _textPaint;
        private readonly global::Android.Graphics.Path _scrimPath = new();
        private readonly float _density;

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
            _textPaint = new Paint(PaintFlags.AntiAlias)
            {
                Color = Color.White,
                TextSize = 15f * _density,
                TextAlign = Paint.Align.Center,
            };
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

            _scrimPath.Reset();
            _scrimPath.SetFillType(global::Android.Graphics.Path.FillType.EvenOdd!);
            _scrimPath.AddRect(0, 0, w, h, global::Android.Graphics.Path.Direction.Cw!);
            _scrimPath.AddRoundRect(new RectF(left, top, right, bottom), radius, radius, global::Android.Graphics.Path.Direction.Cw!);
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

            canvas.DrawText(_hint, w / 2f, bottom + 36f * _density, _textPaint);
        }
    }
}
