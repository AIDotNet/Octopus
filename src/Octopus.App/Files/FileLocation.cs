namespace Octopus.App.Files;

/// <summary>Where a file is and how the info bar writes its name: the folder part (relative to the project when inside it) and the name.</summary>
public sealed record FileLocation(string Path, string Folder, string Label)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    public static FileLocation Of(string path, string? projectRoot)
    {
        var shown = path;
        if (!string.IsNullOrEmpty(projectRoot))
        {
            var relative = System.IO.Path.GetRelativePath(projectRoot, path);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(relative)) shown = relative;
        }
        shown = shown.Replace('\\', '/');
        var cut = shown.LastIndexOf('/');
        return cut < 0 ? new FileLocation(path, "", shown) : new FileLocation(path, shown[..(cut + 1)], shown[(cut + 1)..]);
    }
}
