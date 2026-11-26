using System.Globalization;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Octopus.Mobile.Common;
using Octopus.Mobile.Localization;
using Octopus.Mobile.Shell;
using Octopus.Mobile.Workspace;

namespace Octopus.Mobile.Files;

/// <summary>One line of a file as the viewer shows it.</summary>
public sealed record FileLine(string Number, string Text);

/// <summary>
/// A file of the host, read-only: previews images (with zoom), renders Markdown documents (with a source toggle), and
/// shows source/text files with line numbers in the monospace face (design/ui/移动端功能扩展方案.html, ⑤ "文件查看").
/// A file that is too large or non-previewable binary says so instead.
/// </summary>
public sealed partial class FileViewerViewModel : ObservableObject, IDisposable
{
    /// <summary>The largest text file read: the protocol carries it in 256 KB pieces, and a phone has no use for more.</summary>
    public const long MaxBytes = 1024 * 1024;

    /// <summary>The largest image file read for mobile preview (10 MB).</summary>
    public const long MaxImageBytes = 10 * 1024 * 1024;

    /// <summary>Longest decoded image side kept in memory on mobile so high-res photos never OOM.</summary>
    public const int MaxDecodedImageSide = 2048;

    /// <summary>More lines than this are counted but not shown, so that a log does not freeze the page.</summary>
    public const int MaxLines = 5000;

    /// <summary>A longer line is cut (minified files).</summary>
    public const int MaxLineLength = 1000;

    private static readonly double[] ZoomSteps = [1.0, 1.5, 2.0, 3.0, 4.0];

    private readonly IRemoteWorkspace _workspace;
    private readonly INavigator _navigator;
    private readonly Action<Uri> _openBrowser;
    private bool _disposed;
    private int _zoomIndex;

    /// <param name="location">Where the file is, for the line under its name ("src/Auth"); the folder of its path when null.</param>
    public FileViewerViewModel(RemoteProject project, string path, string? location, IRemoteWorkspace workspace, INavigator navigator, Action<Uri>? openBrowser = null)
    {
        Project = project;
        Path = path;
        _workspace = workspace;
        _navigator = navigator;
        _openBrowser = openBrowser ?? MobileHost.OpenBrowser;
        Title = FilesViewModel.NameOf(path);
        Location = location ?? FilesViewModel.NameOf(path[..Math.Max(0, path.Length - Title.Length)]);
        Lines = [];
    }

    public RemoteProject Project { get; }
    public string Path { get; }
    public string Title { get; }
    public string Location { get; }

    [ObservableProperty] public partial IReadOnlyList<FileLine> Lines { get; private set; }
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    /// <summary>Why there is no preview: non-image binary, too large, corrupt image, or host error.</summary>
    [ObservableProperty] public partial string? Message { get; private set; }
    [ObservableProperty] public partial string? CappedText { get; private set; }
    [ObservableProperty] public partial int LineCount { get; private set; }
    [ObservableProperty] public partial bool IsWrapped { get; set; }

    // Image preview state
    [ObservableProperty] public partial Bitmap? ImageBitmap { get; private set; }
    [ObservableProperty] public partial bool IsImage { get; private set; }
    [ObservableProperty] public partial int ImageWidth { get; private set; }
    [ObservableProperty] public partial int ImageHeight { get; private set; }
    [ObservableProperty] public partial string? ImageFormatName { get; private set; }
    [ObservableProperty] public partial long FileSizeBytes { get; private set; }
    [ObservableProperty] public partial double ZoomScale { get; private set; } = 1.0;

    // Markdown preview state
    [ObservableProperty] public partial bool IsMarkdownFile { get; private set; }
    [ObservableProperty] public partial bool IsMarkdownPreview { get; set; }
    [ObservableProperty] public partial string? MarkdownText { get; private set; }

    public bool HasMessage => Message is not null;
    public bool IsCapped => CappedText is not null && ShowCodeLines;
    public bool ShowImage => IsImage && !HasMessage && ImageBitmap is not null;
    public bool ShowImageFit => ShowImage && IsZoomFit;
    public bool ShowImageZoomed => ShowImage && !IsZoomFit;
    public bool IsZoomFit => _zoomIndex == 0;
    public bool CanZoomIn => _zoomIndex < ZoomSteps.Length - 1;
    public bool CanZoomOut => _zoomIndex > 0;
    public string ZoomLabel => IsZoomFit
        ? MobileLangResources.Get(MobileLangResourceKind.FileImageZoomFit)
        : $"{(int)Math.Round(ZoomScale * 100)}%";
    public double DisplayImageWidth => ImageWidth > 0 ? ImageWidth * ZoomScale : 0;
    public double DisplayImageHeight => ImageHeight > 0 ? ImageHeight * ZoomScale : 0;

    public bool ShowMarkdownPreview => !IsImage && !HasMessage && IsMarkdownFile && IsMarkdownPreview;
    public bool ShowCodeLines => !IsImage && !HasMessage && !ShowMarkdownPreview;
    public bool ShowWrapButton => !IsImage && !ShowMarkdownPreview;
    public bool ShowPreviewToggle => !IsImage && !HasMessage && IsMarkdownFile;

    /// <summary>Under the title: where the file is, read-only, and its dimensions/size or line count once read.</summary>
    public string Subtitle
    {
        get
        {
            if (IsImage && ImageWidth > 0 && ImageHeight > 0)
            {
                return string.Join(" · ", new[]
                {
                    Location,
                    ImageFormatName,
                    $"{ImageWidth}×{ImageHeight}",
                    FileSizeBytes > 0 ? CodeLines.Size(FileSizeBytes) : null,
                }.Where(part => !string.IsNullOrEmpty(part)));
            }
            return string.Join(" · ", new[]
            {
                Location,
                MobileLangResources.Get(MobileLangResourceKind.FileReadOnly),
                LineCount > 0 ? string.Format(MobileLangResources.Get(MobileLangResourceKind.FileLineCount), LineCount) : null,
            }.Where(part => !string.IsNullOrEmpty(part)));
        }
    }

    /// <summary>The width of the number column: as many digits as the last line number has.</summary>
    public double NumberWidth => CodeLines.NumberWidth(LineCount);
    public TextWrapping Wrapping => IsWrapped ? TextWrapping.Wrap : TextWrapping.NoWrap;
    public ScrollBarVisibility HorizontalScroll => IsWrapped ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            if (MobileFileLinks.IsImageExtension(Path))
            {
                await LoadImageAsync();
            }
            else
            {
                await LoadTextAsync();
            }
        }
        catch (IOException e)
        {
            Message = e.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadImageAsync()
    {
        var raw = await _workspace.ReadFileBytesAsync(Project, Path, MaxImageBytes);
        if (_disposed) return;

        if (raw.Kind == RemoteFileBytesKind.TooLarge)
        {
            Message = string.Format(MobileLangResources.Get(MobileLangResourceKind.FileTooLarge), CodeLines.Size(raw.Size), CodeLines.Size(MaxImageBytes));
            return;
        }

        var format = MobileFileLinks.SniffImageFormat(raw.Data);
        var decoded = await Task.Run(() => TryDecodeBitmap(raw.Data));
        if (_disposed)
        {
            decoded?.Bitmap.Dispose();
            return;
        }
        if (decoded is null)
        {
            Message = MobileLangResources.Get(MobileLangResourceKind.FileImageInvalid);
            return;
        }

        ReplaceBitmap(decoded.Value.Bitmap);
        ImageWidth = decoded.Value.Width;
        ImageHeight = decoded.Value.Height;
        ImageFormatName = format ?? System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();
        FileSizeBytes = raw.Size;
        IsImage = true;
        _zoomIndex = 0;
        ZoomScale = 1.0;
        NotifyViewProperties();
    }

    private async Task LoadTextAsync()
    {
        var content = await _workspace.ReadTextFileAsync(Project, Path, MaxBytes);
        if (_disposed) return;

        switch (content.Kind)
        {
            case RemoteFileContentKind.Binary:
                Message = MobileLangResources.Get(MobileLangResourceKind.FileBinary);
                break;
            case RemoteFileContentKind.TooLarge:
                Message = string.Format(MobileLangResources.Get(MobileLangResourceKind.FileTooLarge), CodeLines.Size(content.Size), CodeLines.Size(MaxBytes));
                break;
            default:
                var (lines, total) = CodeLines.Split(content.Text);
                LineCount = total;
                Lines = lines;
                CappedText = total > lines.Count
                    ? string.Format(MobileLangResources.Get(MobileLangResourceKind.LinesCapped), lines.Count, total)
                    : null;
                if (MobileFileLinks.IsMarkdownExtension(Path))
                {
                    MarkdownText = content.Text;
                    IsMarkdownFile = true;
                    IsMarkdownPreview = true;
                }
                NotifyViewProperties();
                break;
        }
    }

    private static (Bitmap Bitmap, int Width, int Height)? TryDecodeBitmap(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        try
        {
            using var probe = new MemoryStream(bytes, writable: false);
            var full = new Bitmap(probe);
            var w = full.PixelSize.Width;
            var h = full.PixelSize.Height;
            if (w <= 0 || h <= 0)
            {
                full.Dispose();
                return null;
            }
            var longest = Math.Max(w, h);
            if (longest <= MaxDecodedImageSide)
            {
                return (full, w, h);
            }
            full.Dispose();
            using var scaledStream = new MemoryStream(bytes, writable: false);
            var scaled = w >= h
                ? Bitmap.DecodeToWidth(scaledStream, MaxDecodedImageSide, BitmapInterpolationMode.HighQuality)
                : Bitmap.DecodeToHeight(scaledStream, MaxDecodedImageSide, BitmapInterpolationMode.HighQuality);
            return (scaled, w, h);
        }
        catch
        {
            return null;
        }
    }

    [RelayCommand]
    private void Back() => _navigator.Back();

    [RelayCommand]
    private void ToggleWrap() => IsWrapped = !IsWrapped;

    [RelayCommand]
    private void TogglePreview()
    {
        if (!IsMarkdownFile) return;
        IsMarkdownPreview = !IsMarkdownPreview;
    }

    [RelayCommand]
    private void ZoomIn()
    {
        if (!IsImage || !CanZoomIn) return;
        _zoomIndex++;
        ZoomScale = ZoomSteps[_zoomIndex];
        NotifyZoomProperties();
    }

    [RelayCommand]
    private void ZoomOut()
    {
        if (!IsImage || !CanZoomOut) return;
        _zoomIndex--;
        ZoomScale = ZoomSteps[_zoomIndex];
        NotifyZoomProperties();
    }

    [RelayCommand]
    private void ResetZoom()
    {
        if (!IsImage) return;
        _zoomIndex = _zoomIndex == 0 ? 2 : 0;
        ZoomScale = ZoomSteps[_zoomIndex];
        NotifyZoomProperties();
    }

    [RelayCommand]
    private void OpenLink(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        if (MobileFileLinks.IsWebUrl(target) && Uri.TryCreate(target.Trim(), UriKind.Absolute, out var uri))
        {
            _openBrowser(uri);
            return;
        }
        var baseDir = MobileFileLinks.ParentDirectoryOf(Path) ?? Project.Path;
        if (MobileFileLinks.ResolveRemotePath(target, baseDir) is { } resolved)
        {
            _navigator.ShowFile(Project, resolved);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReplaceBitmap(null);
    }

    private void ReplaceBitmap(Bitmap? next)
    {
        var prev = ImageBitmap;
        ImageBitmap = next;
        prev?.Dispose();
    }

    private void NotifyViewProperties()
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ShowImage));
        OnPropertyChanged(nameof(ShowImageFit));
        OnPropertyChanged(nameof(ShowImageZoomed));
        OnPropertyChanged(nameof(ShowMarkdownPreview));
        OnPropertyChanged(nameof(ShowCodeLines));
        OnPropertyChanged(nameof(ShowWrapButton));
        OnPropertyChanged(nameof(ShowPreviewToggle));
        OnPropertyChanged(nameof(IsCapped));
        NotifyZoomProperties();
    }

    private void NotifyZoomProperties()
    {
        OnPropertyChanged(nameof(IsZoomFit));
        OnPropertyChanged(nameof(ShowImageFit));
        OnPropertyChanged(nameof(ShowImageZoomed));
        OnPropertyChanged(nameof(CanZoomIn));
        OnPropertyChanged(nameof(CanZoomOut));
        OnPropertyChanged(nameof(ZoomLabel));
        OnPropertyChanged(nameof(DisplayImageWidth));
        OnPropertyChanged(nameof(DisplayImageHeight));
    }

    partial void OnMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasMessage));
        NotifyViewProperties();
    }

    partial void OnCappedTextChanged(string? value) => OnPropertyChanged(nameof(IsCapped));

    partial void OnLineCountChanged(int value)
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(NumberWidth));
    }

    partial void OnIsWrappedChanged(bool value)
    {
        OnPropertyChanged(nameof(Wrapping));
        OnPropertyChanged(nameof(HorizontalScroll));
    }

    partial void OnIsMarkdownPreviewChanged(bool value) => NotifyViewProperties();
}

/// <summary>What the file viewer and the diff page share: lines cut to size, numbers, sizes in words.</summary>
internal static class CodeLines
{
    /// <summary>The lines of a text (at most <see cref="FileViewerViewModel.MaxLines"/>) and how many it has in all. A last empty line after the final newline is not one.</summary>
    public static (IReadOnlyList<FileLine> Lines, int Total) Split(string text)
    {
        var lines = new List<FileLine>();
        var total = 0;
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOf('\n', start);
            if (end < 0) end = text.Length;
            total++;
            if (lines.Count < FileViewerViewModel.MaxLines)
            {
                lines.Add(new FileLine(total.ToString(CultureInfo.InvariantCulture), Shown(text.AsSpan(start, end - start))));
            }
            start = end + 1;
        }
        return (lines, total);
    }

    /// <summary>A line as shown: without its carriage return, tabs as four spaces, cut when very long.</summary>
    public static string Shown(ReadOnlySpan<char> line)
    {
        if (line.Length > 0 && line[^1] == '\r') line = line[..^1];
        var cut = line.Length > FileViewerViewModel.MaxLineLength;
        var text = (cut ? line[..FileViewerViewModel.MaxLineLength] : line).ToString().Replace("\t", "    ", StringComparison.Ordinal);
        return cut ? text + "…" : text;
    }

    /// <summary>The number column of the monospace face at the pages' size: room for the digits of <paramref name="last"/>.</summary>
    public static double NumberWidth(int last) => Math.Max(2, last.ToString(CultureInfo.InvariantCulture).Length) * 7.5 + 4;

    /// <summary>"18 KB", "1.2 MB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " GB",
    };
}
