using Avalonia.Media.Imaging;

namespace Octopus.Terminal.Avalonia;

/// <summary>Clipboard images pasted into a terminal: written as PNG files under the temp folder.</summary>
public static class ClipboardImages
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    /// <summary>Saves <paramref name="bitmap"/> and returns the path, quoted when it contains a space.</summary>
    public static string Save(Bitmap bitmap) => Save(bitmap, Path.Combine(Path.GetTempPath(), "octopus-paste"));

    /// <summary>Saves <paramref name="bitmap"/> into <paramref name="folder"/> and removes files older than a day.</summary>
    public static string Save(Bitmap bitmap, string folder)
    {
        Directory.CreateDirectory(folder);
        Cleanup(folder);
        var path = Path.Combine(folder, $"paste-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        bitmap.Save(path);
        return Quote(path);
    }

    /// <summary>Single quotes keep a path with spaces in one word for a shell; plain paths stay as they are.</summary>
    public static string Quote(string path) => path.Contains(' ') ? $"'{path.Replace("'", "'\\''")}'" : path;

    private static void Cleanup(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "paste-*.png"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > MaxAge) File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
