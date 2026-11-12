namespace Octopus.Platform.Linux;

/// <summary>
/// <c>xdg-open</c> for both operations: file managers have no common "select this file" command, so Reveal opens
/// the containing folder.
/// </summary>
public sealed class LinuxFileOpener : IFileOpener
{
    public void Open(string path) => DetachedProcess.Start("xdg-open", path);

    public bool OpenUrl(string url)
    {
        if (!WebUrl.IsHttp(url)) return false;
        DetachedProcess.Start("xdg-open", url);
        return true;
    }

    public void Reveal(string path)
    {
        var folder = Directory.Exists(path) ? Path.GetDirectoryName(path.TrimEnd('/')) : Path.GetDirectoryName(path);
        DetachedProcess.Start("xdg-open", string.IsNullOrEmpty(folder) ? path : folder);
    }
}
