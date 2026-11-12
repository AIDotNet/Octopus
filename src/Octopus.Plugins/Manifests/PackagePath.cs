namespace Octopus.Plugins.Manifests;

/// <summary>
/// Resolves a path written in a manifest (relative, forward slashes) against the package directory, and refuses any
/// path that leaves the package: absolute paths, <c>..</c> segments that climb out, and symbolic links that point out.
/// </summary>
internal static class PackagePath
{
    public static bool TryResolve(string packageDirectory, string relative, out string fullPath, out string error)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(relative))
        {
            error = "is empty";
            return false;
        }
        if (relative.Contains('\0') || relative.Contains('\\') || Path.IsPathRooted(relative) || relative.Contains(':'))
        {
            error = "must be a relative path with forward slashes inside the package";
            return false;
        }
        var root = Path.GetFullPath(packageDirectory);
        var candidate = Path.GetFullPath(Path.Combine(root, relative));
        if (!IsInside(root, candidate))
        {
            error = "points outside the package";
            return false;
        }
        if (!File.Exists(candidate))
        {
            error = "does not exist";
            return false;
        }
        // A symbolic link inside the package may still point out of it: compare the real locations.
        var realRoot = RealPath(root);
        var realCandidate = RealPath(candidate);
        if (!IsInside(realRoot, realCandidate))
        {
            error = "is a link that points outside the package";
            return false;
        }
        fullPath = candidate;
        error = "";
        return true;
    }

    internal static bool IsInside(string root, string path)
    {
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }

    /// <summary>
    /// The path with every symbolic link on the way resolved, including links in the directories a link points into
    /// (on macOS <c>/var</c> is itself a link to <c>/private/var</c>). Missing parts are kept as written.
    /// </summary>
    internal static string RealPath(string path) => RealPath(path, depth: 0);

    private static string RealPath(string path, int depth)
    {
        var full = Path.GetFullPath(path);
        if (depth > 40)
        {
            return full; // a link cycle; the caller's containment check then decides
        }
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                current = RealPath(target.FullName, depth + 1);
            }
        }
        return current;
    }
}
