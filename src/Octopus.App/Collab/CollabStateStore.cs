using System.Text;
using System.Text.Json.Nodes;
using Octopus.App.Files;
using Octopus.Platform;

namespace Octopus.App.Collab;

/// <summary>
/// Keeps the collaboration ledger (tasks, threads, id counters, what each caller has already seen) in
/// <c>collab/state.json</c> under the data directory, so a restart does not forget delegated work.
/// The document is built with <see cref="JsonObject"/> by the coordinator (no reflection, NativeAOT safe).
/// </summary>
public sealed class CollabStateStore(IAppPaths paths)
{
    private readonly object _writeGate = new();

    public string FilePath => Path.Combine(paths.DataDirectory, "collab", "state.json");

    public JsonObject? Load()
    {
        var path = FilePath;
        try
        {
            if (!File.Exists(path)) return null;
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public void Save(string json)
    {
        lock (_writeGate)
        {
            try
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFileWriter.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(json));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best effort: losing one snapshot only means a restart reports slightly older task state.
            }
        }
    }
}
