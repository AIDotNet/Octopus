using System.Text;

namespace Octopus.FileSystem;

/// <summary>Encoding names, byte order marks and decoding for <see cref="FileAccessService"/>.</summary>
internal static class TextCodec
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly Encoding Utf16Le = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
    private static readonly Encoding Utf16Be = new UnicodeEncoding(bigEndian: true, byteOrderMark: false);
    private static readonly Encoding Utf32Le = new UTF32Encoding(bigEndian: false, byteOrderMark: false);
    private static readonly Encoding Utf32Be = new UTF32Encoding(bigEndian: true, byteOrderMark: false);

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];
    private static ReadOnlySpan<byte> Utf16LeBom => [0xFF, 0xFE];
    private static ReadOnlySpan<byte> Utf16BeBom => [0xFE, 0xFF];

    /// <summary>The encoding for a name a plugin passed, with the name reported back. Unknown: <see cref="FileErrorCode.InvalidArgument"/>.</summary>
    public static (Encoding Encoding, string Name) Resolve(string name)
    {
        switch (name.Trim().ToLowerInvariant().Replace("_", "-", StringComparison.Ordinal))
        {
            case "utf-8" or "utf8":
                return (Utf8, "utf-8");
            case "utf-16le" or "utf16le" or "utf-16" or "utf16" or "ucs-2":
                return (Utf16Le, "utf-16le");
            case "utf-16be" or "utf16be":
                return (Utf16Be, "utf-16be");
            case "utf-32le" or "utf32le" or "utf-32" or "utf32":
                return (Utf32Le, "utf-32le");
            case "utf-32be" or "utf32be":
                return (Utf32Be, "utf-32be");
            case "latin1" or "latin-1" or "iso-8859-1":
                return (Encoding.Latin1, "latin1");
            case "ascii" or "us-ascii":
                return (Encoding.ASCII, "ascii");
        }
        try
        {
            // Whatever else the runtime knows without extra code-page providers.
            var encoding = Encoding.GetEncoding(name);
            return (encoding, encoding.WebName);
        }
        catch (ArgumentException e)
        {
            throw new FileAccessException(FileErrorCode.InvalidArgument, $"Unknown encoding '{name}'.", null, e);
        }
    }

    /// <summary>Decodes <paramref name="bytes"/>: by the named encoding, else by the byte order mark, else as UTF-8.</summary>
    public static FileText Decode(ReadOnlySpan<byte> bytes, string? encodingName)
    {
        if (encodingName is not null)
        {
            var (encoding, name) = Resolve(encodingName);
            var bom = Preamble(name);
            var hasBom = bom.Length > 0 && bytes.StartsWith(bom);
            return new FileText(encoding.GetString(hasBom ? bytes[bom.Length..] : bytes), name, hasBom);
        }
        if (bytes.StartsWith(Utf8Bom)) return new FileText(Utf8.GetString(bytes[3..]), "utf-8", true);
        if (bytes.StartsWith(Utf16LeBom)) return new FileText(Utf16Le.GetString(bytes[2..]), "utf-16le", true);
        if (bytes.StartsWith(Utf16BeBom)) return new FileText(Utf16Be.GetString(bytes[2..]), "utf-16be", true);
        return new FileText(Utf8.GetString(bytes), "utf-8", false);
    }

    /// <summary>The bytes to write: optional byte order mark plus the encoded text. Over <paramref name="maxBytes"/>: <see cref="FileErrorCode.TooLarge"/>.</summary>
    public static byte[] Encode(string text, string? encodingName, bool byteOrderMark, int maxBytes, string path)
    {
        var (encoding, name) = encodingName is null ? (Utf8, "utf-8") : Resolve(encodingName);
        var bom = byteOrderMark ? Preamble(name) : [];
        // Count first so an oversized text is refused before a second copy of it is made.
        var count = (long)encoding.GetByteCount(text) + bom.Length;
        if (count > maxBytes)
        {
            throw new FileAccessException(FileErrorCode.TooLarge, $"{count} bytes is over the {maxBytes} byte limit of one write.", path);
        }
        var bytes = new byte[count];
        bom.CopyTo(bytes);
        encoding.GetBytes(text, bytes.AsSpan(bom.Length));
        return bytes;
    }

    /// <summary>UTF-8 without a byte order mark, for line-based reads and appends.</summary>
    public static Encoding Utf8NoBom => Utf8;

    public static bool StartsWithUtf16Bom(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Utf16LeBom) || bytes.StartsWith(Utf16BeBom);

    /// <summary>The UTF-8 byte order mark, which line readers drop from the first line of a file.</summary>
    public static ReadOnlySpan<byte> Utf8Preamble => Utf8Bom;

    private static ReadOnlySpan<byte> Preamble(string name) => name switch
    {
        "utf-8" => Utf8Bom,
        "utf-16le" => Utf16LeBom,
        "utf-16be" => Utf16BeBom,
        "utf-32le" => [0xFF, 0xFE, 0x00, 0x00],
        "utf-32be" => [0x00, 0x00, 0xFE, 0xFF],
        _ => [],
    };
}
