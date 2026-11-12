namespace Octopus.Platform.Windows;

/// <summary>Explorer opens files with their default app; <c>/select,</c> highlights an item. Not verified on real Windows.</summary>
public sealed class WindowsFileOpener : IFileOpener
{
    public void Open(string path) => DetachedProcess.Start("explorer.exe", path);
    public bool OpenUrl(string url)
    {
        if (!WebUrl.IsHttp(url)) return false;
        DetachedProcess.Start("explorer.exe", url);
        return true;
    }

    public void Reveal(string path) => DetachedProcess.Start("explorer.exe", "/select," + path);
}
