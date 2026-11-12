using Octopus.App.Common;
using Octopus.App.Files.Images;

namespace Octopus.App.Files;

/// <summary>
/// Decides which <see cref="FileKind"/> opens a file: by extension first, and for a name without a known extension by
/// the first bytes of the file (the signature of a picture). A new kind needs a line here and a viewer in <see cref="FileViewers"/>.
/// </summary>
public static class FileKinds
{
    /// <summary>The bytes <see cref="FromHeader"/> needs to see.</summary>
    public const int HeaderLength = 32;

    private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".apng", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".webp", ".bmp", ".ico", ".svg",
    };

    /// <summary>The kind by extension alone; null when the extension says nothing (no extension, or one that is not a picture or Markdown).</summary>
    public static FileKind? FromExtension(string fileName)
    {
        if (FileLanguages.IsMarkdown(fileName)) return FileKind.Markdown;
        var extension = Path.GetExtension(fileName);
        if (s_imageExtensions.Contains(extension)) return FileKind.Image;
        return null;
    }

    /// <summary>The kind by the first bytes: a picture's signature, otherwise <see cref="FileKind.Code"/>.</summary>
    public static FileKind FromHeader(ReadOnlySpan<byte> header) => ImageProbe.Sniff(header) is not null ? FileKind.Image : FileKind.Code;

    /// <summary>The kind of a file: its extension decides when it knows, else the header (read from disk when not given).</summary>
    public static FileKind Detect(string path, ReadOnlySpan<byte> header = default)
    {
        if (FromExtension(path) is { } byExtension) return byExtension;
        // A name that already has a language of its own (.cs, .json...) is text whatever its first bytes look like.
        if (FileLanguages.For(path) != FileLanguages.PlainText) return FileKind.Code;
        if (header.IsEmpty) header = ReadHeader(path);
        return FromHeader(header);
    }

    private static byte[] ReadHeader(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[HeaderLength];
            var read = stream.Read(buffer, 0, buffer.Length);
            return buffer[..read];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
