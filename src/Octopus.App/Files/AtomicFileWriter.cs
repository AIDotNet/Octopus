namespace Octopus.App.Files;

/// <summary>
/// Replaces a file's content without ever leaving it half written: the new bytes go into a temporary file beside it,
/// which then takes its place. A link is followed (the file it points to is replaced, the link stays), and on
/// systems that have them the file's permission bits are kept.
/// </summary>
public static class AtomicFileWriter
{
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        var target = path;
        try
        {
            if (new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true) is { } linked) target = linked.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not a link we can follow: the path itself is written.
        }
        var directory = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            KeepPermissions(target, temp);
            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void KeepPermissions(string from, string to)
    {
        try
        {
            // Windows throws PlatformNotSupportedException, caught below: asking the system which one this is belongs to the composition root.
#pragma warning disable CA1416
            if (File.Exists(from)) File.SetUnixFileMode(to, File.GetUnixFileMode(from));
#pragma warning restore CA1416
        }
        catch (Exception e) when (e is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            // Windows has no permission bits to keep.
        }
    }
}
