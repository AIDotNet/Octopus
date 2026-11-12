namespace Octopus.Platform.Unix;

/// <summary>File checks shared by the Unix locator and shell resolvers.</summary>
public static class UnixFile
{
    /// <summary>
    /// True for a regular file (or a link that resolves to one) with an execute bit. Broken links and directories
    /// are false. Any execute bit counts: the real uid/gid check would need P/Invoke for little gain.
    /// </summary>
    public static bool IsExecutableFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return false; // false for directories and for links that do not resolve
#pragma warning disable CA1416 // Only reached on Unix; this class lives in the Unix helper set.
            var mode = File.GetUnixFileMode(path);
#pragma warning restore CA1416
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The path with every symbolic link resolved; the input when it is not a link.</summary>
    public static string ResolveRealPath(string path)
    {
        try
        {
            var target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? Path.GetFullPath(path);
        }
        catch (IOException)
        {
            return Path.GetFullPath(path);
        }
    }
}
