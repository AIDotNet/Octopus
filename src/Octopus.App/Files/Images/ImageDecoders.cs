using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Svg;
using Svg.Model;
using Svg.Skia;

namespace Octopus.App.Files.Images;

/// <summary>The frames of an animated picture and how long each one shows.</summary>
/// <param name="Repetitions">How many times the animation plays again after the first time; -1 forever.</param>
public sealed record ImageAnimation(IReadOnlyList<Bitmap> Frames, IReadOnlyList<TimeSpan> Durations, int Repetitions);

/// <summary>
/// The decoders beyond Avalonia's own still-picture one: SVG drawn to pixels, and every frame of an animated GIF or WebP.
/// Both run off the UI thread.
/// </summary>
public static class ImageDecoders
{
    /// <summary>The longest side an SVG is drawn at: enough to stay sharp when zoomed, bounded so it stays small in memory.</summary>
    public const int MaxSvgPixels = 4096;

    /// <summary>All decoded frames of an animation together may take this much; a bigger one shows its first frame only.</summary>
    public const long MaxAnimationBytes = 192L * 1024 * 1024;

    public const int MaxFrames = 2000;

    /// <summary>Browsers show a frame that asks for less than this for 100 ms instead, and so do we (many GIFs rely on it).</summary>
    private static readonly TimeSpan MinFrameDuration = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan DefaultFrameDuration = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Nothing outside the file is read: no linked file, no address on the network (a picture in a repository someone
    /// else wrote must not reach out), only the document itself and the data: addresses inside it.
    /// </summary>
    private static readonly SvgParameters s_svgParameters = new(null, null, null,
        new SvgDocumentLoadOptions { ExternalResources = SvgExternalResourcePolicy.SameDocumentAndDataOnly });

    public static bool IsSvg(string path) => string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Draws an SVG at a resolution above its own size (at least twice, a high-density screen at 100%, up to
    /// <see cref="MaxSvgPixels"/>), so zooming in stays sharp for a while. The size given back is the SVG's own, in
    /// pixels at 100%. Null when it does not parse or has no size.
    /// </summary>
    public static (Bitmap Bitmap, ImageInfo Info)? DecodeSvg(byte[] bytes)
    {
        using var svg = new SKSvg();
        SKPicture? picture;
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            picture = svg.Load(stream, s_svgParameters);
        }
        if (picture is null) return null;
        var bounds = picture.CullRect;
        if (!(bounds.Width > 0 && bounds.Height > 0) || float.IsInfinity(bounds.Width) || float.IsInfinity(bounds.Height)) return null;

        var width = Math.Max(1, (int)Math.Ceiling(bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(bounds.Height));
        var longest = Math.Max(bounds.Width, bounds.Height);
        var scale = Math.Min(MaxSvgPixels / longest, Math.Max(2, 2048 / longest));
        var pixelWidth = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        var pixelHeight = Math.Max(1, (int)Math.Round(bounds.Height * scale));

        using var bitmap = new SKBitmap(new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Transparent);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Scale((float)(pixelWidth / bounds.Width), (float)(pixelHeight / bounds.Height));
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
            canvas.Flush();
        }
        return (ToAvalonia(bitmap), new ImageInfo(ImageFormat.Svg, width, height));
    }

    /// <summary>
    /// Every frame of an animated GIF or WebP, each one composed over the frames it builds on; null for a still picture,
    /// one whose frames would not fit in <see cref="MaxAnimationBytes"/>, or one the decoder cannot read.
    /// </summary>
    public static ImageAnimation? DecodeAnimation(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.FrameCount < 2) return null;
        var frames = codec.FrameInfo;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        if (info.Width <= 0 || info.Height <= 0) return null;
        // The decoder's frames and the copies handed to Avalonia live together for a moment: count both.
        if (frames.Length > MaxFrames || (long)frames.Length * info.BytesSize64 * 2 > MaxAnimationBytes) return null;

        var decoded = new SKBitmap?[frames.Length];
        var result = new List<Bitmap>(frames.Length);
        try
        {
            for (var i = 0; i < frames.Length; i++)
            {
                var bitmap = new SKBitmap(info);
                decoded[i] = bitmap;
                var required = frames[i].RequiredFrame;
                if (required >= 0 && required < i && decoded[required] is { } basis)
                {
                    // The frame draws over an earlier one: start from it.
                    basis.GetPixelSpan().CopyTo(bitmap.GetPixelSpan());
                }
                else
                {
                    bitmap.Erase(SKColors.Transparent);
                    required = -1;
                }
                var status = codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(i, required));
                // A frame cut short shows what was read, as a browser does; anything else is a broken picture.
                if (status is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return Dispose(result);
            }
            foreach (var bitmap in decoded) result.Add(ToAvalonia(bitmap!));
        }
        finally
        {
            foreach (var bitmap in decoded) bitmap?.Dispose();
        }
        var durations = frames.Select(f => TimeSpan.FromMilliseconds(f.Duration) is var d && d >= MinFrameDuration ? d : DefaultFrameDuration).ToList();
        return new ImageAnimation(result, durations, codec.RepetitionCount);
    }

    private static ImageAnimation? Dispose(List<Bitmap> bitmaps)
    {
        foreach (var bitmap in bitmaps) bitmap.Dispose();
        return null;
    }

    /// <summary>Copies Skia's pixels into an Avalonia bitmap (Bgra8888, premultiplied: the same layout).</summary>
    private static Bitmap ToAvalonia(SKBitmap bitmap) =>
        new(PixelFormat.Bgra8888, AlphaFormat.Premul, bitmap.GetPixels(), new PixelSize(bitmap.Width, bitmap.Height), new Vector(96, 96), bitmap.RowBytes);
}
