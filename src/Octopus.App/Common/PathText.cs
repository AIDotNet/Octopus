namespace Octopus.App.Common;

/// <summary>How a path is shown in text.</summary>
public static class PathText
{
    /// <summary>The last segment of a folder path ("/work/atlas/" or "C:\work\atlas" gives "atlas"); the whole path for a root.</summary>
    public static string NameOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        if (trimmed.Length == 0) return path;
        var index = trimmed.LastIndexOfAny(['/', '\\']);
        return index < 0 ? trimmed : trimmed[(index + 1)..];
    }
}
