namespace Octopus.Platform.Unix;

/// <summary>What the macOS and Linux trash have in common: a volume check, collision-free names, and a plain rename.</summary>
public static class UnixTrash
{
    /// <summary>errno for "cross-device link": rename(2) cannot move between file systems.</summary>
    private const int CrossDevice = 18;

    /// <summary>
    /// True when <paramref name="item"/> (its directory, really: a link is moved as a link) and
    /// <paramref name="trashDirectory"/> are on the same mounted file system, decided by the longest mount point
    /// that contains each real path. A rename only works within one; across volumes the item would have to be copied,
    /// which is not what a trash does.
    /// </summary>
    public static bool SameVolume(string item, string trashDirectory)
    {
        IReadOnlyList<string> mounts;
        try
        {
            mounts = [.. DriveInfo.GetDrives().Select(d => d.Name)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true; // cannot tell: the rename itself refuses to cross devices (see TryRename)
        }
        var itemDirectory = Path.GetDirectoryName(Path.GetFullPath(item)) ?? item;
        return string.Equals(MountOf(RealPath(itemDirectory), mounts), MountOf(RealPath(trashDirectory), mounts), StringComparison.Ordinal);
    }

    /// <summary>The longest of <paramref name="mounts"/> that is <paramref name="path"/> or one of its ancestors.</summary>
    public static string MountOf(string path, IEnumerable<string> mounts)
    {
        var best = "/";
        foreach (var mount in mounts)
        {
            var trimmed = mount.Length > 1 ? mount.TrimEnd('/') : mount;
            var inside = path == trimmed || path.StartsWith(trimmed == "/" ? "/" : trimmed + "/", StringComparison.Ordinal);
            if (inside && trimmed.Length > best.Length) best = trimmed;
        }
        return best;
    }

    /// <summary>An entry is there: a file, a directory, or a link (also one whose target is missing).</summary>
    public static bool EntryExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Renames <paramref name="from"/> to <paramref name="to"/>, moving the entry itself (a link stays a link). Never
    /// copies: false when the two are on different devices. Throws when <paramref name="to"/> exists.
    /// </summary>
    public static bool TryRename(string from, string to)
    {
        try
        {
            // Directory.Move is rename(2) for files, directories and links alike, and it has no copy fallback.
            Directory.Move(from, to);
            return true;
        }
        catch (IOException e) when (e.HResult == CrossDevice)
        {
            return false;
        }
    }

    /// <summary>
    /// <paramref name="name"/> for the first try, then numbered variants: <c>notes 2.txt</c> with
    /// <paramref name="separator"/> " " (Finder), <c>notes.2.txt</c> with "." (GLib).
    /// </summary>
    public static string NumberedName(string name, int number, string separator)
    {
        if (number <= 1) return name;
        var extension = Path.GetExtension(name);
        // A leading dot is part of the name (".bashrc"), not an extension.
        if (extension.Length == name.Length) extension = "";
        return name[..^extension.Length] + separator + number + extension;
    }

    /// <summary>The path with links resolved segment by segment; parts that do not exist are kept as written.</summary>
    private static string RealPath(string path, int depth = 0)
    {
        var full = Path.GetFullPath(path);
        if (depth > 40) return full;
        var root = Path.GetPathRoot(full) ?? "/";
        var current = root;
        foreach (var part in full[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                var info = new FileInfo(current);
                if (info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = RealPath(target.FullName, depth + 1);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // unreadable or a cycle: keep the path as written
            }
        }
        return current;
    }
}
