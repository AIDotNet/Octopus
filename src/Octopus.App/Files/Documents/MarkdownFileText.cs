using System.Text;

namespace Octopus.App.Files.Documents;

/// <summary>
/// A Markdown file as text plus the way it is written on disk: the byte order mark, the line ending and whether it ends
/// with a newline. The editor works on plain LF text; <see cref="Encode"/> puts a saved document back in the file's own style.
/// </summary>
public sealed record MarkdownFileText(string Text, bool HasBom, string NewLine, bool EndsWithNewline, bool HasMixedNewLines)
{
    private static readonly UTF8Encoding s_strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>An empty file: nothing yet, so a first save ends the text with a newline.</summary>
    public static MarkdownFileText Empty { get; } = new("", false, "\n", true, false);

    /// <summary>Reads the bytes as UTF-8; false when they are not valid UTF-8 (saving such a file as text would damage it).</summary>
    public static bool TryDecode(byte[] bytes, out MarkdownFileText document)
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
        document = new MarkdownFileText(text, hasBom, newLine, text.Length == 0 || text.EndsWith('\n'), mixed);
        return true;
    }

    /// <summary>The file's bytes for a document the editor exported: this file's BOM, line ending and final newline.</summary>
    public byte[] Encode(string markdown)
    {
        var body = markdown.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n');
        if (body.Length > 0 && EndsWithNewline) body += "\n";
        if (NewLine != "\n") body = body.Replace("\n", NewLine);
        var text = new UTF8Encoding(false).GetBytes(body);
        if (!HasBom) return text;
        var withBom = new byte[text.Length + 3];
        withBom[0] = 0xEF;
        withBom[1] = 0xBB;
        withBom[2] = 0xBF;
        text.CopyTo(withBom, 3);
        return withBom;
    }
}
