using System.Text;

namespace Octopus.App.Files.Code;

/// <summary>
/// A source or text file as normalized LF text plus the way it is written on disk: the byte order mark and the
/// dominant line ending. Unlike Markdown's rich-text export, <see cref="Encode"/> keeps whatever trailing newlines the
/// editor currently holds, only restoring the file's line ending style and BOM.
/// </summary>
public sealed record CodeFileText(string Text, bool HasBom, string NewLine, bool HasMixedNewLines)
{
    private static readonly UTF8Encoding s_strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>An empty UTF-8 LF document with no BOM.</summary>
    public static CodeFileText Empty { get; } = new("", false, "\n", false);

    /// <summary>Reads the bytes as strict UTF-8; returns false when invalid UTF-8 so editing cannot corrupt the file.</summary>
    public static bool TryDecode(byte[] bytes, out CodeFileText document)
    {
        document = Empty;
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        string raw;
        try
        {
            raw = s_strict.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\r')
            {
                if (i + 1 < raw.Length && raw[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (raw[i] == '\n') lf++;
        }
        var newLine = crlf > lf && crlf >= cr ? "\r\n" : cr > lf && cr > crlf ? "\r" : "\n";
        var mixed = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0) > 1;
        var text = crlf + cr == 0 ? raw : raw.Replace("\r\n", "\n").Replace('\r', '\n');
        document = new CodeFileText(text, hasBom, newLine, mixed);
        return true;
    }

    /// <summary>Encodes the editor's text back into bytes with this file's original BOM and line ending style.</summary>
    public byte[] Encode(string text)
    {
        var body = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (NewLine != "\n") body = body.Replace("\n", NewLine);
        var encoded = new UTF8Encoding(false).GetBytes(body);
        if (!HasBom) return encoded;
        var withBom = new byte[encoded.Length + 3];
        withBom[0] = 0xEF;
        withBom[1] = 0xBB;
        withBom[2] = 0xBF;
        encoded.CopyTo(withBom, 3);
        return withBom;
    }
}
