using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.App.Localization;

namespace Octopus.App.Files.Images;

/// <summary>
/// A picture: decoded off the UI thread, shown fitted to the window and zoomable. A very large picture or a damaged
/// one is not decoded (or not shown) and says so instead. The zoom arithmetic is <see cref="ImageViewport"/>.
/// </summary>
public sealed partial class ImageViewerModel : FileViewerModel
{
    /// <summary>A file above this is not read (a decoded picture is far bigger than its file).</summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    /// <summary>A picture with more pixels than this is not decoded: 100 million pixels is 400 MB once decoded.</summary>
    public const long MaxPixels = 100_000_000;

    private int _version;
    private Size _viewSize;

    public ImageViewerModel(FileViewerContext context) : base(context)
    {
    }

    /// <summary>The decoded picture; null while loading and when there is a notice instead.</summary>
    public Bitmap? Image { get; private set; }

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
        : string.Join(" · ", ImageProbe.FormatName(Info.Format), $"{Info.Width:N0} × {Info.Height:N0}", FileReader.FormatSize(FileSize));

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
            result.Bitmap?.Dispose();
            return;
        }
        var old = Image;
        if (result.Problem is { } problem)
        {
            Image = null;
            Info = result.Info;
            Viewport = null;
            FileSize = result.Size;
            Notice = result.Argument is { } arg ? AppLangResources.Format(problem, arg.Width.ToString("N0"), arg.Height.ToString("N0")) : AppLangResources.Get(problem);
            OnPropertyChanged(nameof(MetaText));
            ViewChanged?.Invoke(this, EventArgs.Empty);
            old?.Dispose();
            return;
        }
        // The same picture size again keeps the zoom the person chose; a different one starts fitted.
        var keep = Viewport is { IsFit: false } existing && Info is { } before && result.Info is { } after
                   && before.Width == after.Width && before.Height == after.Height ? existing : null;
        Image = result.Bitmap;
        Info = result.Info;
        FileSize = result.Size;
        Viewport = keep ?? new ImageViewport(result.Info!.Width, result.Info.Height);
        Notice = null;
        OnPropertyChanged(nameof(MetaText));
        Change(v => v.SetView(_viewSize));
        old?.Dispose();
    }

    private sealed record Decoded(Bitmap? Bitmap, ImageInfo? Info, long Size, AppLangResourceKind? Problem, ImageInfo? Argument);

    private Decoded Decode()
    {
        var read = FileReader.Read(Path, MaxFileBytes);
        if (read.Problem is { } problem)
        {
            return new Decoded(null, null, read.Size, problem == AppLangResourceKind.FileTooLarge ? AppLangResourceKind.ImageFileTooLarge : problem, null);
        }
        var bytes = read.Bytes!;
        var info = ImageProbe.TryRead(bytes);
        if (info is null) return new Decoded(null, null, bytes.Length, AppLangResourceKind.ImageBroken, null);
        if (info.Pixels > MaxPixels) return new Decoded(null, info, bytes.Length, AppLangResourceKind.ImageTooManyPixels, info);
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
        Image?.Dispose();
        Image = null;
    }
}
