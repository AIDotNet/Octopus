namespace Octopus.App.Common;

/// <summary>How a path is shown in text.</summary>
public static class PathText
{
    /// <summary>The last segment of a folder path ("/work/atlas/" gives "atlas"); the whole path for a root.</summary>
    public static string NameOf(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
