namespace Octopus.Platform.MacOS;

/// <summary>Uses <c>open</c>; <c>open -R</c> selects the item in Finder.</summary>
public sealed class MacFileOpener : IFileOpener
{
    public void Open(string path) => DetachedProcess.Start("/usr/bin/open", path);
    public bool OpenUrl(string url)
    {
        if (!WebUrl.IsHttp(url)) return false;
        DetachedProcess.Start("/usr/bin/open", url);
        return true;
    }

    public void Reveal(string path) => DetachedProcess.Start("/usr/bin/open", "-R", path);
}
