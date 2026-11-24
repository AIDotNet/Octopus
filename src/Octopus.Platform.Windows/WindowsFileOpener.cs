using System.ComponentModel;
using System.Diagnostics;

namespace Octopus.Platform.Windows;

/// <summary>Explorer opens files with their default app; <c>/select,</c> highlights an item.</summary>
public sealed class WindowsFileOpener : IFileOpener
{
    public void Open(string path) => DetachedProcess.Start("explorer.exe", path);

    /// <summary>
    /// Hands the address to the shell, which opens it in the default browser. <c>explorer.exe &lt;url&gt;</c> is not
    /// used: given an address with a query (the sign-in page's <c>?code=</c>) it opens a File Explorer window instead.
    /// </summary>
    public bool OpenUrl(string url)
    {
        if (!WebUrl.IsHttp(url)) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public void Reveal(string path) => DetachedProcess.Start("explorer.exe", "/select," + path);
}
