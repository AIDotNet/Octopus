namespace Octopus.App.Files.Documents;

/// <summary>Finds the picture behind an address written in a Markdown file: a relative one starts at the file's folder.</summary>
public static class MarkdownImagePath
{
    /// <summary>The largest picture file read for a document.</summary>
    public const long MaxBytes = 20L * 1024 * 1024;

    /// <summary>The path on disk for a relative or absolute file address; null for anything the editor loads itself (http, data, avares, file URLs).</summary>
    public static string? Resolve(string folder, string source)
    {
        if (string.IsNullOrWhiteSpace(source) || folder.Length == 0) return null;
        var address = source.Trim();
        var cut = address.IndexOfAny(['#', '?']);
        if (cut >= 0) address = address[..cut];
        if (address.Length == 0) return null;
        if (Path.IsPathRooted(address)) return address;
        // "https:", "data:", "avares:", "file:" and friends have a scheme; a one-letter "scheme" is a Windows drive.
        var colon = address.IndexOf(':');
        if (colon > 1) return null;
        try
        {
            return Path.GetFullPath(Path.Combine(folder, Uri.UnescapeDataString(address)));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>The picture's bytes as a stream, or null when the file is missing, unreadable or too large.</summary>
    public static Stream? Open(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxBytes) return null;
            return new MemoryStream(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
