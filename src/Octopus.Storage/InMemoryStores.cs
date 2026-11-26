using Octopus.Core.Agents;
using Octopus.Core.Bots;
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

    public void Flush()
    {
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

/// <summary>An account store that keeps <c>accounts.json</c> in memory (headless tests).</summary>
public sealed class InMemoryAccountStore(AccountsDocument? initial = null) : IAccountStore
{
    public AccountsDocument Current { get; private set; } = JsonAccountStore.Normalize(initial);
    public event EventHandler? Changed;

    public void Load() => Changed?.Invoke(this, EventArgs.Empty);

    public void Update(Action<AccountsDocument> mutate)
    {
        mutate(Current);
        Current = JsonAccountStore.Normalize(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>A bot store that keeps <c>bots.json</c> in memory (headless tests).</summary>
public sealed class InMemoryBotStore(BotsDocument? initial = null) : IBotStore
{
    public BotsDocument Current { get; private set; } = JsonBotStore.Normalize(initial);
    public event EventHandler? Changed;

    public void Load() => Changed?.Invoke(this, EventArgs.Empty);

    public void Update(Action<BotsDocument> mutate)
    {
        mutate(Current);
        Current = JsonBotStore.Normalize(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
