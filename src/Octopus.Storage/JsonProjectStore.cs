using System.Text.Json;
using Octopus.Core.Projects;
using Octopus.Platform;

namespace Octopus.Storage;

/// <summary>Stores projects in <c>&lt;DataDirectory&gt;/projects.json</c>.</summary>
public sealed class JsonProjectStore(IAppPaths paths) : IProjectStore
{
    private readonly string _path = Path.Combine(paths.DataDirectory, "projects.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return new ProjectsDocument();
            string text;
            try { text = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new ProjectsDocument(); }
            try
            {
                var document = JsonSerializer.Deserialize(text, OctopusJsonContext.FileOptions.GetTypeInfo<ProjectsDocument>());
                if (document is null) throw new JsonException("null document");
                return ProjectsDocumentNormalizer.Normalize(document);
            }
            catch (JsonException)
            {
                AtomicFile.Quarantine(_path);
                return new ProjectsDocument();
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var text = JsonSerializer.Serialize(document, OctopusJsonContext.FileOptions.GetTypeInfo<ProjectsDocument>());
            await Task.Run(() => AtomicFile.WriteAllText(_path, text), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
