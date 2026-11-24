using Avalonia.Media.Imaging;

namespace Octopus.Terminal.Avalonia;

/// <summary>
/// Pictures handed to a terminal as a path: clipboard images pasted here, and pictures a phone uploads for a session on
/// this computer. They are kept in one folder under the temp folder and removed after a day.
/// </summary>
public static class ClipboardImages
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    /// <summary>The picture types a terminal is handed, by extension (lower case, with the dot).</summary>
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".webp"];

    /// <summary>Where the pictures are kept.</summary>
    public static string DefaultFolder { get; } = Path.Combine(Path.GetTempPath(), "octopus-paste");

    /// <summary>Saves <paramref name="bitmap"/> and returns the path, quoted when it contains a space.</summary>
    public static string Save(Bitmap bitmap) => Save(bitmap, DefaultFolder);

    /// <summary>Saves <paramref name="bitmap"/> into <paramref name="folder"/> and removes files older than a day.</summary>
    public static string Save(Bitmap bitmap, string folder)
    {
        Directory.CreateDirectory(folder);
        Cleanup(folder);
        var path = NewPath(folder, ".png");
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        return Quote(path);
    }

    /// <summary>A new picture's path in <paramref name="folder"/>: <c>paste-</c>, the time to the millisecond, and the extension.</summary>
    public static string NewPath(string folder, string extension)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var path = Path.Combine(folder, $"paste-{stamp}{extension}");
        // Two pictures within the same millisecond (two phones, say) each get their own file.
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"paste-{stamp}-{n}{extension}");
        return path;
    }

    /// <summary>The extension of a picture's file name when it is a picture type a terminal is handed; null otherwise.</summary>
    public static string? ImageExtension(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        return Array.IndexOf(Extensions, extension) >= 0 ? extension : null;
    }

    /// <summary>Single quotes keep a path with spaces in one word for a shell; plain paths stay as they are.</summary>
    public static string Quote(string path) => path.Contains(' ') ? $"'{path.Replace("'", "'\\''")}'" : path;

    /// <summary>Removes pictures, and uploads that never finished, older than a day.</summary>
    public static void Cleanup(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (!name.StartsWith("paste-", StringComparison.Ordinal) && !name.StartsWith("upload-", StringComparison.Ordinal)) continue;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > MaxAge) File.Delete(file);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
