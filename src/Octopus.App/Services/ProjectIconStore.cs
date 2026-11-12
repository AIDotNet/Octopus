using Avalonia.Media.Imaging;
using Octopus.Platform;
using SkiaSharp;

namespace Octopus.App.Services;

/// <summary>
/// The pictures of project squares. A chosen image is never shown or kept as it is: it is cropped to its centre
/// square and scaled down to a <see cref="ThumbnailSize"/> px PNG under <c>&lt;DataDirectory&gt;/project-icons</c>,
/// so a 20 MB photo costs a few kilobytes on disk and in memory.
/// </summary>
public interface IProjectIconStore
{
    /// <summary>Makes the thumbnail of <paramref name="sourcePath"/>; returns its file name, or null when the file is not a readable image.</summary>
    Task<string?> ImportAsync(string projectId, string sourcePath);

    /// <summary>The thumbnail as a bitmap (cached); null when the file is missing or unreadable.</summary>
    Bitmap? Load(string? fileName);

    /// <summary>Deletes a thumbnail Octopus made. Names that are not plain file names are ignored.</summary>
    void Delete(string? fileName);
}

public sealed class ProjectIconStore(IAppPaths paths) : IProjectIconStore
{
    /// <summary>Edge of the stored square: the 34 px square at up to 3x scale, with room to spare.</summary>
    public const int ThumbnailSize = 128;

    /// <summary>Larger source files are not decoded.</summary>
    public const long MaxSourceBytes = 50L * 1024 * 1024;

    private readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.Ordinal);

    public string Directory => Path.Combine(paths.DataDirectory, "project-icons");

    public Task<string?> ImportAsync(string projectId, string sourcePath) => Task.Run(() =>
    {
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists || info.Length == 0 || info.Length > MaxSourceBytes)
            {
                return null;
            }
            using var thumbnail = MakeThumbnail(sourcePath, ThumbnailSize);
            if (thumbnail is null)
            {
                return null;
            }
            System.IO.Directory.CreateDirectory(Directory);
            // A new name on every change, so nothing shows a stale cached picture.
            var name = $"{SafeId(projectId)}-{Guid.NewGuid().ToString("N")[..8]}.png";
            var path = Path.Combine(Directory, name);
            using (var data = thumbnail.Encode(SKEncodedImageFormat.Png, 100))
            using (var file = File.Create(path))
            {
                data.SaveTo(file);
            }
            return name;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    });

    public Bitmap? Load(string? fileName)
    {
        if (!IsOwnName(fileName))
        {
            return null;
        }
        if (_cache.TryGetValue(fileName!, out var cached))
        {
            return cached;
        }
        Bitmap? bitmap = null;
        try
        {
            var path = Path.Combine(Directory, fileName!);
            if (File.Exists(path))
            {
                using var stream = File.OpenRead(path);
                // The file is already small; decoding to the thumbnail size also bounds an edited or foreign file.
                bitmap = Bitmap.DecodeToWidth(stream, ThumbnailSize, BitmapInterpolationMode.HighQuality);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            bitmap = null;
        }
        _cache[fileName!] = bitmap;
        return bitmap;
    }

    public void Delete(string? fileName)
    {
        if (!IsOwnName(fileName))
        {
            return;
        }
        if (_cache.Remove(fileName!, out var bitmap))
        {
            bitmap?.Dispose();
        }
        try
        {
            File.Delete(Path.Combine(Directory, fileName!));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Decodes the image at the smallest size the codec offers that still covers <paramref name="size"/>, turns it
    /// upright (EXIF orientation of photos), and draws its centre square scaled to <paramref name="size"/>.
    /// </summary>
    public static SKImage? MakeThumbnail(string sourcePath, int size)
    {
        using var codec = SKCodec.Create(sourcePath);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            return null;
        }
        var origin = codec.EncodedOrigin;
        var shortSide = Math.Min(codec.Info.Width, codec.Info.Height);
        // Codecs (JPEG, WebP) decode at 1/2, 1/4, 1/8 directly: the full photo never sits in memory.
        var scale = Math.Min(1f, (float)size / shortSide);
        var dimensions = codec.GetScaledDimensions(scale);
        if (Math.Min(dimensions.Width, dimensions.Height) < size)
        {
            dimensions = codec.Info.Size;
        }
        var info = new SKImageInfo(dimensions.Width, dimensions.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(info);
        var result = codec.GetPixels(info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            return null;
        }

        // Upright size: a rotated photo swaps width and height.
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        float width = swap ? decoded.Height : decoded.Width;
        float height = swap ? decoded.Width : decoded.Height;
        var edge = Math.Min(width, height);
        var factor = size / edge;

        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
        {
            return null;
        }
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        // Centre the upright image on the square, scaled so its short side fills it.
        canvas.Translate(size / 2f, size / 2f);
        canvas.Scale(factor);
        canvas.Translate(-width / 2f, -height / 2f);
        ApplyOrigin(canvas, origin, decoded.Width, decoded.Height);
        using var image = SKImage.FromBitmap(decoded);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawImage(image, new SKRect(0, 0, decoded.Width, decoded.Height), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        canvas.Flush();
        return surface.Snapshot();
    }

    /// <summary>Maps the stored pixels (w × h) into upright space, per the EXIF orientation.</summary>
    private static void ApplyOrigin(SKCanvas canvas, SKEncodedOrigin origin, int w, int h)
    {
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, h); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.Scale(-1, 1); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(h, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(h, w); canvas.Scale(1, -1); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, w); canvas.RotateDegrees(270); break;
        }
    }

    private static string SafeId(string id) => new([.. id.Where(char.IsAsciiLetterOrDigit).Take(32)]);

    /// <summary>A plain file name in the icon folder: nothing that climbs out of it.</summary>
    private static bool IsOwnName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name == Path.GetFileName(name) && !name.Contains("..", StringComparison.Ordinal);
}
