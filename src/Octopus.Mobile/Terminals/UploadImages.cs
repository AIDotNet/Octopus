using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Octopus.Remote.Protocol;
using Octopus.Terminal.Avalonia;

namespace Octopus.Mobile.Terminals;

/// <summary>A picture the person chose on the phone: its file name and its bytes as they are.</summary>
public sealed record PickedImage(string Name, byte[] Data);

/// <summary>The picture stays larger than a host takes (<see cref="RemoteMethods.UploadMaxBytes"/>) even when made smaller.</summary>
public sealed class ImageTooLargeException() : IOException("The picture is too large.");

/// <summary>
/// Readies a picture from the phone for a session's program on the host. A small PNG, JPEG, GIF or WebP goes as it is
/// (a screenshot stays sharp, a GIF keeps moving); anything else (a camera photo, a HEIC) is turned upright by its EXIF
/// orientation, scaled to at most <see cref="MaxEdge"/> pixels on its longer side, put on white and sent as a JPEG.
/// </summary>
public static class UploadImages
{
    /// <summary>The longer side of a picture that has to be made smaller: enough for an agent to read a screenshot.</summary>
    public const int MaxEdge = 2048;
    /// <summary>A picture of a type the host takes, upright, no larger than this and no wider than <see cref="KeepEdge"/>, goes as it is.</summary>
    public const long KeepBytes = 1_500_000;
    public const int KeepEdge = 2560;
    public const int JpegQuality = 85;

    /// <summary>The picture to upload. Throws <see cref="InvalidDataException"/> when it cannot be read or stays too large.</summary>
    public static PickedImage Prepare(PickedImage picked)
    {
        var extension = ClipboardImages.ImageExtension(picked.Name);
        Bitmap decoded;
        try
        {
            using var stream = new MemoryStream(picked.Data, writable: false);
            decoded = new Bitmap(stream);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Not something this phone can draw: a type the host takes still goes as it is.
            if (extension is not null && picked.Data.Length <= RemoteMethods.UploadMaxBytes) return picked;
            throw new InvalidDataException("The picture cannot be read.", e);
        }

        using (decoded)
        {
            var orientation = ExifOrientation(picked.Data);
            var size = decoded.PixelSize;
            var edge = Math.Max(size.Width, size.Height);
            var keep = extension is not null
                && orientation == 1
                && (picked.Data.Length <= KeepBytes || extension == ".gif")
                && picked.Data.Length <= RemoteMethods.UploadMaxBytes
                && edge <= KeepEdge;
            if (keep) return picked;

            var scale = Math.Min(1.0, (double)MaxEdge / edge);
            var scaledSize = new PixelSize(Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale)));
            using var scaled = scaledSize == size ? null : decoded.CreateScaledBitmap(scaledSize);
            var data = EncodeUpright(scaled ?? decoded, orientation);
            if (data.Length > RemoteMethods.UploadMaxBytes) throw new ImageTooLargeException();
            return new PickedImage(Path.ChangeExtension(SafeName(picked.Name), ".jpg"), data);
        }
    }

    /// <summary>The EXIF orientation of a JPEG (1 to 8); 1, upright, for anything else or when it has none.</summary>
    public static int ExifOrientation(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return 1;
        var at = 2;
        while (at + 4 <= jpeg.Length && jpeg[at] == 0xFF)
        {
            var marker = jpeg[at + 1];
            if (marker == 0xDA || marker == 0xD9) break; // The image data starts: no more headers.
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(at + 2)..]);
            if (length < 2 || at + 2 + length > jpeg.Length) break;
            var segment = jpeg.Slice(at + 4, length - 2);
            if (marker == 0xE1 && segment.Length > 14 && segment[..6].SequenceEqual("Exif\0\0"u8))
                return OrientationOfTiff(segment[6..]);
            at += 2 + length;
        }
        return 1;
    }

    private static int OrientationOfTiff(ReadOnlySpan<byte> tiff)
    {
        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I') little = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M') little = false;
        else return 1;
        ushort U16(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
        uint U32(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);

        var ifd = U32(tiff[4..]);
        if (ifd + 2 > tiff.Length) return 1;
        var count = U16(tiff[(int)ifd..]);
        for (var i = 0; i < count; i++)
        {
            var entry = (int)ifd + 2 + i * 12;
            if (entry + 12 > tiff.Length) break;
            if (U16(tiff[entry..]) != 0x0112) continue;
            var value = U16(tiff[(entry + 8)..]);
            return value is >= 1 and <= 8 ? value : 1;
        }
        return 1;
    }

    /// <summary>The bitmap's pixels turned upright for <paramref name="orientation"/>, put on white, as a JPEG.</summary>
    private static byte[] EncodeUpright(Bitmap source, int orientation)
    {
        var size = source.PixelSize;
        var stride = size.Width * 4;
        var pixels = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { source.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, stride); }
        finally { handle.Free(); }

        var format = source.Format ?? PixelFormat.Bgra8888;
        var premultiplied = source.AlphaFormat != AlphaFormat.Unpremul;
        // Orientations 5 to 8 swap the sides.
        var swap = orientation >= 5;
        var (width, height) = swap ? (size.Height, size.Width) : (size.Width, size.Height);
        var upright = new byte[width * height * 4];
        for (var y = 0; y < size.Height; y++)
        {
            for (var x = 0; x < size.Width; x++)
            {
                var (tx, ty) = orientation switch
                {
                    2 => (size.Width - 1 - x, y),
                    3 => (size.Width - 1 - x, size.Height - 1 - y),
                    4 => (x, size.Height - 1 - y),
                    5 => (y, x),
                    6 => (size.Height - 1 - y, x),
                    7 => (size.Height - 1 - y, size.Width - 1 - x),
                    8 => (y, size.Width - 1 - x),
                    _ => (x, y),
                };
                var from = y * stride + x * 4;
                var to = (ty * width + tx) * 4;
                // On white: what is see-through in a PNG would be black in a JPEG.
                var alpha = pixels[from + 3];
                for (var c = 0; c < 3; c++)
                {
                    var channel = pixels[from + c];
                    upright[to + c] = premultiplied
                        ? (byte)Math.Min(255, channel + (255 - alpha))
                        : (byte)((channel * alpha + 255 * (255 - alpha) + 127) / 255);
                }
                upright[to + 3] = 255;
            }
        }

        var pinned = GCHandle.Alloc(upright, GCHandleType.Pinned);
        try
        {
            using var result = new Bitmap(format, AlphaFormat.Opaque, pinned.AddrOfPinnedObject(), new PixelSize(width, height), new Vector(96, 96), width * 4);
            using var output = new MemoryStream();
            result.Save(output, new JpegBitmapEncoderOptions { Quality = JpegQuality });
            return output.ToArray();
        }
        finally { pinned.Free(); }
    }

    // Only a file name for the host to take the extension from; the folders of the phone stay on the phone.
    private static string SafeName(string name) => Path.GetFileName(name) is { Length: > 0 } file ? file : "picture";
}
