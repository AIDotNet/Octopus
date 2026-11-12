namespace Octopus.Storage;

/// <summary>Crash-safe file helpers: write to a temp file, then replace.</summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new System.Text.UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static bool TryReadAllText(string path, out string text)
    {
        try
        {
            text = File.ReadAllText(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            text = "";
            return false;
        }
    }

    /// <summary>Renames a damaged file to <c>name.corrupt-yyyyMMddHHmmss</c> so it is kept for inspection.</summary>
    public static void Quarantine(string path)
    {
        try
        {
            var target = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            for (var n = 1; File.Exists(target); n++) target = $"{path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}-{n}";
            File.Move(path, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
