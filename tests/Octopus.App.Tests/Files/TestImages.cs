using SkiaSharp;

namespace Octopus.App.Tests.Files;

/// <summary>Pictures made in code for the tests: nothing is downloaded or checked in.</summary>
internal static class TestImages
{
    /// <summary>A picture with a gradient, a few shapes and a transparent corner, so fitting, zooming and the checkerboard are visible.</summary>
    public static SKBitmap Draw(int width, int height, bool transparentCorner = true)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        using (var paint = new SKPaint { IsAntialias = true })
        {
            paint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height),
                [new SKColor(0x4F, 0x46, 0xE5), new SKColor(0x06, 0xB6, 0xD4), new SKColor(0xF5, 0x9E, 0x0B)], SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(new SKRect(0, 0, width, height), width / 12f, width / 12f, paint);
            paint.Shader = null;
            paint.Color = SKColors.White.WithAlpha(220);
            canvas.DrawCircle(width * 0.33f, height * 0.4f, Math.Min(width, height) * 0.2f, paint);
            paint.Color = new SKColor(0x11, 0x18, 0x27, 230);
            canvas.DrawRect(new SKRect(width * 0.5f, height * 0.55f, width * 0.88f, height * 0.85f), paint);
            if (transparentCorner)
            {
                paint.BlendMode = SKBlendMode.Clear;
                canvas.DrawCircle(width * 0.9f, height * 0.12f, Math.Min(width, height) * 0.12f, paint);
            }
        }
        return bitmap;
    }

    public static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format, int quality = 90)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        return data.ToArray();
    }

    public static byte[] Png(int width, int height) { using var b = Draw(width, height); return Encode(b, SKEncodedImageFormat.Png); }
    public static byte[] Jpeg(int width, int height) { using var b = Draw(width, height); return Encode(b, SKEncodedImageFormat.Jpeg); }
    public static byte[] WebP(int width, int height) { using var b = Draw(width, height); return Encode(b, SKEncodedImageFormat.Webp, 80); }

    /// <summary>A 24-bit BMP written by hand (Skia does not encode BMP).</summary>
    public static byte[] Bmp(int width, int height)
    {
        using var bitmap = Draw(width, height, transparentCorner: false);
        var rowSize = (width * 3 + 3) & ~3;
        var dataSize = rowSize * height;
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write((byte)'B'); w.Write((byte)'M'); w.Write(14 + 40 + dataSize); w.Write(0); w.Write(14 + 40);
        w.Write(40); w.Write(width); w.Write(height); w.Write((short)1); w.Write((short)24); w.Write(0); w.Write(dataSize); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        var padding = new byte[rowSize - width * 3];
        for (var y = height - 1; y >= 0; y--)
        {
            for (var x = 0; x < width; x++) { var c = bitmap.GetPixel(x, y); w.Write(c.Blue); w.Write(c.Green); w.Write(c.Red); }
            w.Write(padding);
        }
        return stream.ToArray();
    }

    /// <summary>An .ico holding one PNG entry (the form Windows uses for 256-pixel icons, and the one every decoder reads).</summary>
    public static byte[] Ico(int size)
    {
        var png = Png(size, size);
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write((short)0); w.Write((short)1); w.Write((short)1);
        w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)(size >= 256 ? 0 : size)); w.Write((byte)0); w.Write((byte)0);
        w.Write((short)1); w.Write((short)32); w.Write(png.Length); w.Write(6 + 16);
        w.Write(png);
        return stream.ToArray();
    }

    /// <summary>The smallest GIF there is: one pixel.</summary>
    public static byte[] Gif() => Convert.FromBase64String("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7");

    /// <summary>The header of a PNG that claims a size and has no pixels: what a decompression bomb looks like to a probe.</summary>
    public static byte[] PngHeaderOnly(uint width, uint height)
    {
        using var stream = new MemoryStream();
        stream.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R']);
        var be = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(be, width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(be.AsSpan(4), height);
        stream.Write(be);
        stream.Write([8, 6, 0, 0, 0, 0, 0, 0, 0]);
        return stream.ToArray();
    }
}
