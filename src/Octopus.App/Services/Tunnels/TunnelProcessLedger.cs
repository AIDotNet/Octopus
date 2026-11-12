using System.Diagnostics;
using System.Globalization;
using Octopus.Platform;

namespace Octopus.App.Services;

/// <summary>
/// The ssh processes the tunnels started, written down as they start. When the app ends without stopping them (it
/// crashed, it was killed) they go on running and keep their ports; the next start finds them here and ends them
/// (design/11-SSH隧道.md §6.4). One file per app process, named by its id, so that a second copy of the app does not end
/// the tunnels of the first: a file is only acted on when the app process that wrote it is gone.
/// Best effort: a file that cannot be read or written costs a leftover process, nothing more.
/// </summary>
internal sealed class TunnelProcessLedger(string directory, int ownerId)
{
    private const string ProcessName = "ssh";

    private readonly object _gate = new();
    private readonly List<string> _lines = [];

    private string OwnFile => Path.Combine(directory, ownerId.ToString(CultureInfo.InvariantCulture) + ".txt");

    public void Add(IRunningProcess process)
    {
        lock (_gate)
        {
            _lines.Add(Line(process.Id, process.StartedAt));
            Save();
        }
    }

    public void Remove(IRunningProcess process)
    {
        lock (_gate)
        {
            if (_lines.RemoveAll(l => l.StartsWith(process.Id.ToString(CultureInfo.InvariantCulture) + "|", StringComparison.Ordinal)) > 0) Save();
        }
    }

    /// <summary>Ends the processes that the files of app processes that are gone name, and deletes those files. Returns how many were ended.</summary>
    public int EndLeftovers(IProcessHost host)
    {
        var ended = 0;
        string[] files;
        try
        {
            files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.txt") : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        foreach (var file in files)
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), NumberStyles.None, CultureInfo.InvariantCulture, out var owner)
                || owner == ownerId
                || IsRunning(owner))
            {
                continue;
            }
            try
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    var parts = line.Split('|');
                    if (parts.Length == 2
                        && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                        && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var started)
                        && host.KillIfSame(id, DateTimeOffset.FromUnixTimeMilliseconds(started), ProcessName))
                    {
                        ended++;
                    }
                }
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return ended;
    }

    private void Save()
    {
        try
        {
            if (_lines.Count == 0)
            {
                File.Delete(OwnFile);
                return;
            }
            Directory.CreateDirectory(directory);
            File.WriteAllLines(OwnFile, _lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Line(int id, DateTimeOffset startedAt) =>
        id.ToString(CultureInfo.InvariantCulture) + "|" + startedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
