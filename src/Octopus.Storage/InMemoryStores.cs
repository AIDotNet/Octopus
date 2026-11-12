using Octopus.Core.Projects;
using Octopus.Core.Settings;

namespace Octopus.Storage;

/// <summary>A settings store that never touches the disk (headless tests, designer).</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    public AppSettingsModel Current { get; private set; } = new();
    public event EventHandler? Changed;

    public void Load() => Changed?.Invoke(this, EventArgs.Empty);

    public void Update(Action<AppSettingsModel> mutate)
    {
        mutate(Current);
        Current = SettingsNormalizer.Normalize(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A project store that keeps the document in memory.</summary>
public sealed class InMemoryProjectStore(ProjectsDocument? initial = null) : IProjectStore
{
    private ProjectsDocument _document = initial ?? new ProjectsDocument();

    public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_document);

    public Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken = default)
    {
        _document = document;
        return Task.CompletedTask;
    }
}
