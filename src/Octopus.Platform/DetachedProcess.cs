using System.ComponentModel;
using System.Diagnostics;

namespace Octopus.Platform;

/// <summary>Fire-and-forget launch of a helper such as open/xdg-open/explorer.</summary>
public static class DetachedProcess
{
    public static void Start(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        try
        {
            // The opener exits on its own; disposing only releases our handle. A missing opener is not worth a crash.
            using var process = Process.Start(startInfo);
        }
        catch (Win32Exception) { }
    }
}
