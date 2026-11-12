using Octopus.App.Localization;

namespace Octopus.App.Files;

/// <summary>The outcome of reading a file for a viewer: its bytes, or the reason there are none.</summary>
public readonly record struct FileRead(byte[]? Bytes, AppLangResourceKind? Problem, long Size)
{
    public bool Ok => Bytes is not null;
}

/// <summary>Reads a whole file for a viewer without locking it (the editor of the person may be writing it).</summary>
public static class FileReader
{
    /// <summary>Synchronous; callers run it on a worker thread. A file over <paramref name="maxBytes"/> is not read.</summary>
    public static FileRead Read(string path, long maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new FileRead(null, AppLangResourceKind.FileMissing, 0);
            if (info.Length > maxBytes) return new FileRead(null, AppLangResourceKind.FileTooLarge, info.Length);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            return new FileRead(bytes, null, bytes.Length);
        }
        catch (FileNotFoundException)
        {
            return new FileRead(null, AppLangResourceKind.FileMissing, 0);
        }
        catch (DirectoryNotFoundException)
        {
            return new FileRead(null, AppLangResourceKind.FileMissing, 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new FileRead(null, AppLangResourceKind.FileUnreadable, 0);
        }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => bytes + " B",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / (1024.0 * 1024)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB",
    };
}
