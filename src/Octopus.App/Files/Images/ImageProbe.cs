using System.Buffers.Binary;

namespace Octopus.App.Files.Images;

/// <summary>The formats the viewer shows. <see cref="Svg"/> is known by its extension only (it is text, with no signature).</summary>
public enum ImageFormat { Png, Jpeg, Gif, WebP, Bmp, Ico, Svg }

/// <summary>What the header of a picture says, before any pixel is decoded.</summary>
public sealed record ImageInfo(ImageFormat Format, int Width, int Height)
{
    public long Pixels => (long)Width * Height;
}

/// <summary>
/// Reads the format and the pixel size of a picture from its header bytes alone, so a huge picture can be refused
/// before it is decoded (a decoder would allocate all of it) and a name without an extension can still be recognized.
/// </summary>
public static class ImageProbe
{
    public static string FormatName(ImageFormat format) => format switch
    {
        ImageFormat.Png => "PNG",
        ImageFormat.Jpeg => "JPEG",
        ImageFormat.Gif => "GIF",
        ImageFormat.WebP => "WebP",
        ImageFormat.Bmp => "BMP",
        ImageFormat.Ico => "ICO",
        ImageFormat.Svg => "SVG",
        _ => "",
    };

    /// <summary>The format whose signature the bytes start with; null when none does.</summary>
    public static ImageFormat? Sniff(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 8 && h[0] == 0x89 && h[1] == 'P' && h[2] == 'N' && h[3] == 'G' && h[4] == 0x0D && h[5] == 0x0A && h[6] == 0x1A && h[7] == 0x0A) return ImageFormat.Png;
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return ImageFormat.Jpeg;
        if (h.Length >= 6 && h[0] == 'G' && h[1] == 'I' && h[2] == 'F' && h[3] == '8' && (h[4] == '7' || h[4] == '9') && h[5] == 'a') return ImageFormat.Gif;
        if (h.Length >= 12 && h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P') return ImageFormat.WebP;
        // "BM" alone is two ordinary letters: the size of the info header that follows has to be one of the known ones too.
        if (h.Length >= 18 && h[0] == 'B' && h[1] == 'M' && BinaryPrimitives.ReadUInt32LittleEndian(h[14..]) is 12 or 40 or 52 or 56 or 64 or 108 or 124) return ImageFormat.Bmp;
        if (h.Length >= 6 && h[0] == 0 && h[1] == 0 && h[2] == 1 && h[3] == 0 && h[5] == 0 && h[4] is >= 1 and <= 64) return ImageFormat.Ico;
        return null;
    }

    /// <summary>
    /// True when the file ends where a PNG or a JPEG would not: no IEND chunk, no end-of-image marker near the end. A decoder
    /// would show the part it could read (often an empty picture); the viewer calls that damaged instead.
    /// </summary>
    public static bool LooksTruncated(ReadOnlySpan<byte> bytes, ImageFormat format)
    {
        var tail = bytes[Math.Max(0, bytes.Length - 4096)..];
        return format switch
        {
            ImageFormat.Png => tail.IndexOf("IEND"u8) < 0,
            ImageFormat.Jpeg => tail.IndexOf(new byte[] { 0xFF, 0xD9 }) < 0,
            _ => false,
        };
    }

    /// <summary>The format and size from the header; null when the bytes are not a picture or the header is cut short.</summary>
    public static ImageInfo? TryRead(ReadOnlySpan<byte> b)
    {
        if (Sniff(b) is not { } format) return null;
        (int, int)? size = format switch
        {
            ImageFormat.Png => Png(b),
            ImageFormat.Gif => b.Length >= 10 ? (BinaryPrimitives.ReadUInt16LittleEndian(b[6..]), BinaryPrimitives.ReadUInt16LittleEndian(b[8..])) : null,
            ImageFormat.Bmp => Bmp(b),
            ImageFormat.WebP => WebP(b),
            ImageFormat.Jpeg => Jpeg(b),
            ImageFormat.Ico => Ico(b),
            _ => null,
        };
        return size is { } s && s.Item1 > 0 && s.Item2 > 0 ? new ImageInfo(format, s.Item1, s.Item2) : null;
    }

    private static (int, int)? Png(ReadOnlySpan<byte> b)
    {
        if (b.Length < 24) return null;
        var w = BinaryPrimitives.ReadUInt32BigEndian(b[16..]);
        var h = BinaryPrimitives.ReadUInt32BigEndian(b[20..]);
        return w > int.MaxValue || h > int.MaxValue ? null : ((int)w, (int)h);
    }

    private static (int, int)? Bmp(ReadOnlySpan<byte> b)
    {
        var dib = BinaryPrimitives.ReadUInt32LittleEndian(b[14..]);
        if (dib == 12) return b.Length >= 22 ? (BinaryPrimitives.ReadUInt16LittleEndian(b[18..]), BinaryPrimitives.ReadUInt16LittleEndian(b[20..])) : null;
        if (b.Length < 26) return null;
        var w = BinaryPrimitives.ReadInt32LittleEndian(b[18..]);
        var h = BinaryPrimitives.ReadInt32LittleEndian(b[22..]);
        return (w == int.MinValue ? 0 : Math.Abs(w), h == int.MinValue ? 0 : Math.Abs(h));
    }

    private static (int, int)? WebP(ReadOnlySpan<byte> b)
    {
        if (b.Length < 30) return null;
        if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == ' ')
        {
            return (BinaryPrimitives.ReadUInt16LittleEndian(b[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(b[28..]) & 0x3FFF);
        }
        if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'L' && b[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(b[21..]);
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }
        if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'X')
        {
            return ((b[24] | b[25] << 8 | b[26] << 16) + 1, (b[27] | b[28] << 8 | b[29] << 16) + 1);
        }
        return null;
    }

    private static (int, int)? Jpeg(ReadOnlySpan<byte> b)
    {
        var i = 2;
        while (i + 9 < b.Length)
        {
            if (b[i] != 0xFF) { i++; continue; }
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                return (BinaryPrimitives.ReadUInt16BigEndian(b[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..]));
            }
            i += 2 + BinaryPrimitives.ReadUInt16BigEndian(b[(i + 2)..]);
        }
        return null;
    }

    private static (int, int)? Ico(ReadOnlySpan<byte> b)
    {
        int count = b[4], width = 0, height = 0;
        for (var n = 0; n < count && 6 + (n + 1) * 16 <= b.Length; n++)
        {
            var entry = b[(6 + n * 16)..];
            width = Math.Max(width, entry[0] == 0 ? 256 : entry[0]);
            height = Math.Max(height, entry[1] == 0 ? 256 : entry[1]);
        }
        return width > 0 ? (width, height) : null;
    }
}
