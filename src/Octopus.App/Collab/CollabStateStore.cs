using System.Text;
using Octopus.App.Files;
using Octopus.App.Services;
using Octopus.Platform;

namespace Octopus.App.Collab;

/// <summary>
/// Keeps the collaboration ledger (tasks, threads, id counters, what each caller has already seen) in
/// <c>collab/state.json</c> under the data directory, so a restart does not forget delegated work.
/// The document is built with <see cref="JsonObject"/> by the coordinator (no reflection, NativeAOT safe).
/// </summary>
public sealed class CollabStateStore(IAppPaths paths)
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private readonly object _writeGate = new();

    public string FilePath => Path.Combine(paths.DataDirectory, "collab", "state.json");

    /// <summary>The saved ledger, or null when there is none or it cannot be read.</summary>
    public string? LoadText()
    {
        try
        {
            var path = FilePath;
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Save(string json)
    {
        lock (_writeGate)
        {
            try
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFileWriter.WriteAllBytes(path, Utf8NoBom.GetBytes(json));
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                StartupTrace.LogException("collab:state-save", e);
                return false;
            }
        }
    }
}
