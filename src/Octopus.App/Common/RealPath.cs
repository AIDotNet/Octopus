namespace Octopus.App.Common;

/// <summary>
/// Follows the symbolic links of a path (on macOS <c>/var</c> and <c>/tmp</c> are links), because git reports
/// the real location of a repository and file paths from git are matched against the project's path.
/// </summary>
public static class RealPath
{
    public static string Resolve(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? "";
            var current = root;
            foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                if (new DirectoryInfo(current).ResolveLinkTarget(true) is { } target)
                {
                    current = target.FullName;
                }
            }
            return current.Length > root.Length ? current.TrimEnd(Path.DirectorySeparatorChar) : current;
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary><paramref name="path"/> relative to <paramref name="root"/> with forward slashes; null when it lies outside.</summary>
    public static string? Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }
        return relative.Replace('\\', '/');
    }
}
