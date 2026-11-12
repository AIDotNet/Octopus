using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Localization;

namespace Octopus.App.Files.Images;

/// <summary>
/// A picture: decoded off the UI thread, shown fitted to the window and zoomable. A very large picture or a damaged
/// one is not decoded (or not shown) and says so instead. The zoom arithmetic is <see cref="ImageViewport"/>.
/// An SVG is drawn to pixels above its own size; an animated GIF or WebP plays while it is on screen.
/// </summary>
public sealed partial class ImageViewerModel : FileViewerModel
{
    /// <summary>A file above this is not read (a decoded picture is far bigger than its file).</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    /// <summary>A picture with more pixels than this is not decoded: 100 million pixels is 400 MB once decoded.</summary>
    public const long MaxPixels = 100_000_000;

    private int _version;
    private Size _viewSize;
    private ImageAnimation? _animation;
    private int _frame;
    private int _played;
    private bool _onScreen;
    private DispatcherTimer? _timer;

    public ImageViewerModel(FileViewerContext context) : base(context)
    {
        MonoFontFamily = context.MonoFontFamily;
        FontSize = context.FontSize;
        IsSvg = ImageDecoders.IsSvg(context.Path);
    }

    public const string RenderedMode = "rendered";
    public const string SourceMode = "source";

    /// <summary>An SVG is text too: it can be shown as its source.</summary>
    public bool IsSvg { get; }

    public string? MonoFontFamily { get; }
    public double FontSize { get; }

    /// <summary>The SVG's text, for the source view; empty for other pictures.</summary>
    [ObservableProperty] public partial string SourceText { get; private set; } = "";

    /// <summary><see cref="RenderedMode"/> or <see cref="SourceMode"/> (an SVG only).</summary>
    [ObservableProperty] public partial string ViewMode { get; set; } = RenderedMode;

    /// <summary>The picture, its zoom buttons, or the notice about it.</summary>
    public bool ShowRendered => !ShowSource;

    /// <summary>The SVG's source shows instead (also when it could not be drawn: the text is still there to read).</summary>
    public bool ShowSource => IsSvg && ViewMode == SourceMode && SourceText.Length > 0;

    public bool IsRenderedMode => ViewMode == RenderedMode;
    public bool IsSourceMode => ViewMode == SourceMode;

    /// <summary>The bar's two icons: the rendered view or the source.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SetViewMode(string? mode)
    {
        if (mode is RenderedMode or SourceMode) ViewMode = mode;
    }

    public bool ShowModeSwitch => IsSvg && SourceText.Length > 0;
    public bool ShowZoom => HasContent && ShowRendered;
    public bool ShowCanvas => HasContent && ShowRendered;
    public bool ShowNotice => HasNotice && ShowRendered;

    partial void OnViewModeChanged(string value) => RaiseModes();
    partial void OnSourceTextChanged(string value) => RaiseModes();
    protected override void NoticeChanged() => RaiseModes();

    private void RaiseModes()
    {
        OnPropertyChanged(nameof(IsRenderedMode));
        OnPropertyChanged(nameof(IsSourceMode));
        OnPropertyChanged(nameof(ShowRendered));
        OnPropertyChanged(nameof(ShowSource));
        OnPropertyChanged(nameof(ShowModeSwitch));
        OnPropertyChanged(nameof(ShowZoom));
        OnPropertyChanged(nameof(ShowCanvas));
        OnPropertyChanged(nameof(ShowNotice));
    }

    /// <summary>The decoded picture (of an animation: the frame showing now); null while loading and when there is a notice instead.</summary>
    public Bitmap? Image { get; private set; }

    /// <summary>The picture has more than one frame.</summary>
    public bool IsAnimated => _animation is not null;

    public int FrameCount => _animation?.Frames.Count ?? 1;

    /// <summary>The index of the frame showing.</summary>
    public int CurrentFrame => _frame;

    /// <summary>The animation advances (while the picture is on screen); a finished one stops on its last frame.</summary>
    [ObservableProperty] public partial bool IsPlaying { get; private set; }

    public string PlayPauseText => AppLangResources.Get(IsPlaying ? AppLangResourceKind.ImagePause : AppLangResourceKind.ImagePlay);

    /// <summary>Another frame of the animation shows: the canvas draws again.</summary>
    public event EventHandler? FrameChanged;

    public ImageInfo? Info { get; private set; }
    public ImageViewport? Viewport { get; private set; }

    [ObservableProperty] public partial long FileSize { get; private set; }
    [ObservableProperty] public partial string ZoomText { get; private set; } = "100%";
    [ObservableProperty] public partial bool IsFit { get; private set; } = true;

    /// <summary>The picture or its position changed: the canvas draws again.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>"PNG · 640 × 480 · 34 KB": what the bar says quietly about the file.</summary>
    public string MetaText => Info is null
        ? ""
        : string.Join(" · ", new[]
        {
            ImageProbe.FormatName(Info.Format),
            $"{Info.Width:N0} × {Info.Height:N0}",
            IsAnimated ? AppLangResources.Format(AppLangResourceKind.ImageFrames, FrameCount.ToString("N0")) : null,
            FileReader.FormatSize(FileSize),
        }.OfType<string>());

    partial void OnIsPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlayPauseText));
        Schedule();
    }

    /// <summary>Pauses a playing animation; plays a paused one (from the start when it had finished).</summary>
    [RelayCommand]
    private void TogglePlay()
    {
        if (_animation is null) return;
        if (!IsPlaying && _animation.Repetitions >= 0 && _played > _animation.Repetitions)
        {
            _played = 0;
            ShowFrame(0);
        }
        IsPlaying = !IsPlaying;
    }

    /// <summary>The canvas is (or is no longer) in a window: an animation only spends time while it can be seen.</summary>
    public void SetOnScreen(bool onScreen)
    {
        _onScreen = onScreen;
        Schedule();
    }

    private void Schedule()
    {
        if (_animation is null || !IsPlaying || !_onScreen || Disposed)
        {
            _timer?.Stop();
            return;
        }
        _timer ??= CreateTimer();
        _timer.Interval = _animation.Durations[_frame];
        if (!_timer.IsEnabled) _timer.Start();
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render);
        timer.Tick += (_, _) => Advance();
        return timer;
    }

    /// <summary>The next frame; after the last one the animation starts again, as many times as the file says.</summary>
    private void Advance()
    {
        // A tick already due when the animation was paused or hidden does nothing.
        if (_animation is not { } animation || !IsPlaying || !_onScreen || Disposed) return;
        var next = _frame + 1;
        if (next >= animation.Frames.Count)
        {
            _played++;
            if (animation.Repetitions >= 0 && _played > animation.Repetitions)
            {
                IsPlaying = false;
                return;
            }
            next = 0;
        }
        ShowFrame(next);
        Schedule();
    }

    private void ShowFrame(int index)
    {
        if (_animation is null) return;
        _frame = index;
        Image = _animation.Frames[index];
        OnPropertyChanged(nameof(CurrentFrame));
        FrameChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnFileSizeChanged(long value) => OnPropertyChanged(nameof(MetaText));

    [RelayCommand] private void ZoomIn() => Change(v => v.ZoomIn());
    [RelayCommand] private void ZoomOut() => Change(v => v.ZoomOut());
    [RelayCommand] private void ActualSize() => Change(v => v.ActualSize());
    [RelayCommand] private void Fit() => Change(v => v.Fit());
    [RelayCommand] private void ToggleFit() => Change(v => v.ToggleFitActual());

    public void ZoomByWheel(double delta, Point anchor) => Change(v => v.ZoomByWheel(delta, anchor));
    public void ZoomByFactor(double factor, Point anchor) => Change(v => v.ZoomByFactor(factor, anchor));
    public void Pan(Vector delta) => Change(v => v.Pan(delta));

    /// <summary>The canvas has a size (or a new one): fit follows it.</summary>
    public void SetViewSize(Size size)
    {
        _viewSize = size;
        Change(v => v.SetView(size));
    }

    private void Change(Action<ImageViewport> change)
    {
        if (Viewport is null) return;
        change(Viewport);
        ZoomText = Viewport.Percent + "%";
        IsFit = Viewport.IsFit;
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public override async Task LoadAsync()
    {
        var version = ++_version;
        Decoded result;
        try
        {
            result = await Task.Run(Decode);
        }
        catch (Exception)
        {
            result = new Decoded(null, null, 0, AppLangResourceKind.ImageBroken, null);
        }
        if (version != _version || Disposed)
        {
            Release(result.Bitmap, result.Animation);
            return;
        }
        SourceText = result.Source ?? "";
        var old = Image;
        var oldAnimation = _animation;
        var wasPaused = oldAnimation is not null && !IsPlaying;
        // The same animation read again (the file was touched, or rewritten alike) carries on where it was.
        var sameFrames = oldAnimation is not null && result.Animation is not null && oldAnimation.Frames.Count == result.Animation.Frames.Count;
        _animation = result.Animation;
        if (!sameFrames)
        {
            _frame = 0;
            _played = 0;
        }
        OnPropertyChanged(nameof(IsAnimated));
        OnPropertyChanged(nameof(FrameCount));
        OnPropertyChanged(nameof(CurrentFrame));
        if (result.Problem is { } problem)
        {
            Image = null;
            Info = result.Info;
            Viewport = null;
            FileSize = result.Size;
            Notice = result.Argument is { } arg ? AppLangResources.Format(problem, arg.Width.ToString("N0"), arg.Height.ToString("N0")) : AppLangResources.Get(problem);
            OnPropertyChanged(nameof(MetaText));
            ViewChanged?.Invoke(this, EventArgs.Empty);
            IsPlaying = false;
            Release(old, oldAnimation);
            return;
        }
        // The same picture size again keeps the zoom the person chose; a different one starts fitted.
        var keep = Viewport is { IsFit: false } existing && Info is { } before && result.Info is { } after
                   && before.Width == after.Width && before.Height == after.Height ? existing : null;
        Image = _animation is not null ? _animation.Frames[_frame] : result.Bitmap;
        Info = result.Info;
        FileSize = result.Size;
        Viewport = keep ?? new ImageViewport(result.Info!.Width, result.Info.Height);
        Notice = null;
        OnPropertyChanged(nameof(MetaText));
        Change(v => v.SetView(_viewSize));
        // A pause the person chose outlasts a reload.
        IsPlaying = _animation is not null && !wasPaused;
        Schedule();
        Release(old, oldAnimation);
    }

    /// <summary>Frees a picture no longer shown: the still one, or every frame of an animation (the still one is among them).</summary>
    private static void Release(Bitmap? bitmap, ImageAnimation? animation)
    {
        if (animation is not null)
        {
            foreach (var frame in animation.Frames) frame.Dispose();
        }
        else
        {
            bitmap?.Dispose();
        }
    }

    private sealed record Decoded(Bitmap? Bitmap, ImageInfo? Info, long Size, AppLangResourceKind? Problem, ImageInfo? Argument, ImageAnimation? Animation = null)
    {
        /// <summary>An SVG's text (also when it could not be drawn).</summary>
        public string? Source { get; init; }
    }

    private Decoded Decode()
    {
        var read = FileReader.Read(Path, MaxFileBytes);
        if (read.Problem is { } problem)
        {
            return new Decoded(null, null, read.Size, problem == AppLangResourceKind.FileTooLarge ? AppLangResourceKind.ImageFileTooLarge : problem, null);
        }
        var bytes = read.Bytes!;
        if (IsSvg)
        {
            var source = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
            (Bitmap Bitmap, ImageInfo Info)? svg;
            try
            {
                svg = ImageDecoders.DecodeSvg(bytes);
            }
            catch (Exception)
            {
                svg = null;
            }
            return svg is { } drawn
                ? new Decoded(drawn.Bitmap, drawn.Info, bytes.Length, null, null) { Source = source }
                : new Decoded(null, null, bytes.Length, AppLangResourceKind.ImageBroken, null) { Source = source };
        }
        var info = ImageProbe.TryRead(bytes);
        if (info is null) return new Decoded(null, null, bytes.Length, AppLangResourceKind.ImageBroken, null);
        if (info.Pixels > MaxPixels) return new Decoded(null, info, bytes.Length, AppLangResourceKind.ImageTooManyPixels, info);
        if (ImageProbe.LooksTruncated(bytes, info.Format)) return new Decoded(null, info, bytes.Length, AppLangResourceKind.ImageBroken, null);
        if (info.Format is ImageFormat.Gif or ImageFormat.WebP && ImageDecoders.DecodeAnimation(bytes) is { } animation)
        {
            var first = animation.Frames[0];
            return new Decoded(first, new ImageInfo(info.Format, first.PixelSize.Width, first.PixelSize.Height), bytes.Length, null, null, animation);
        }
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new Bitmap(stream);
            // The decoder is the authority on the size (a header can lie, and a GIF's first frame may be smaller).
            var actual = new ImageInfo(info.Format, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            return new Decoded(bitmap, actual, bytes.Length, null, null);
        }
        catch (Exception)
        {
            return new Decoded(null, info, bytes.Length, AppLangResourceKind.ImageBroken, null);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _timer?.Stop();
        _timer = null;
        Release(Image, _animation);
        _animation = null;
        Image = null;
    }
}
