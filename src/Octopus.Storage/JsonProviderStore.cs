using System.Text.Json;
using Octopus.Core.Agents;
using Octopus.Platform;

namespace Octopus.Storage;

/// <summary>Stores non-secret model provider metadata and pinned launch presets in <c>&lt;DataDirectory&gt;/providers.json</c>.</summary>
public sealed class JsonProviderStore(IAppPaths paths) : IProviderStore
{
    private readonly string _path = Path.Combine(paths.DataDirectory, "providers.json");
    private readonly object _gate = new();
    private bool _loaded;

    public ProvidersDocument Current
    {
        get
        {
            EnsureLoaded();
            return field;
        }
        private set;
    } = new();

    public Exception? LastError { get; private set; }

    public event EventHandler? Changed;

    public void Load()
    {
        lock (_gate)
        {
            Current = Read();
            _loaded = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Action<ProvidersDocument> mutate)
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                Current = Read();
                _loaded = true;
            }
            mutate(Current);
            Current = Normalize(Current);
            try
            {
                var text = JsonSerializer.Serialize(Current, OctopusJsonContext.FileTypeInfo<ProvidersDocument>());
                AtomicFile.WriteAllText(_path, text);
                LastError = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LastError = e;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            Current = Read();
            _loaded = true;
        }
    }

    private ProvidersDocument Read()
    {
        if (!File.Exists(_path) || !AtomicFile.TryReadAllText(_path, out var text)) return new ProvidersDocument();
        try
        {
            return Normalize(JsonSerializer.Deserialize(text, OctopusJsonContext.FileTypeInfo<ProvidersDocument>()));
        }
        catch (JsonException)
        {
            AtomicFile.Quarantine(_path);
            return new ProvidersDocument();
        }
    }

    public static ProvidersDocument Normalize(ProvidersDocument? document)
    {
        document ??= new ProvidersDocument();
        document.Providers ??= [];
        var seenProviders = new HashSet<string>(StringComparer.Ordinal);
        document.Providers.RemoveAll(p =>
            p is null
            || string.IsNullOrWhiteSpace(p.Id)
            || !seenProviders.Add(p.Id.Trim()));

        foreach (var provider in document.Providers)
        {
            provider.Id = provider.Id.Trim();
            provider.Name = string.IsNullOrWhiteSpace(provider.Name) ? provider.Id : provider.Name.Trim();
            provider.BaseUrl = (provider.BaseUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(provider.AuthEnvPreference)) provider.AuthEnvPreference = null;
            else provider.AuthEnvPreference = provider.AuthEnvPreference.Trim();

            provider.Headers ??= [];
            foreach (var key in provider.Headers.Keys.Where(k => string.IsNullOrWhiteSpace(k)).ToList())
            {
                provider.Headers.Remove(key);
            }

            provider.Models ??= [];
            var seenModels = new HashSet<string>(StringComparer.Ordinal);
            provider.Models.RemoveAll(m =>
                m is null
                || string.IsNullOrWhiteSpace(m.Id)
                || !seenModels.Add(m.Id.Trim()));

            foreach (var model in provider.Models)
            {
                model.Id = model.Id.Trim();
                model.DisplayName = string.IsNullOrWhiteSpace(model.DisplayName) ? null : model.DisplayName.Trim();
                if (model.SupportedEfforts is { Count: > 0 })
                {
                    model.SupportedEfforts = model.SupportedEfforts
                        .Where(e => !string.IsNullOrWhiteSpace(e))
                        .Select(e => e.Trim().ToLowerInvariant())
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    if (model.SupportedEfforts.Count == 0) model.SupportedEfforts = null;
                }
                else
                {
                    model.SupportedEfforts = null;
                }
            }
        }

        document.PinnedPresets ??= [];
        var seenPresets = new HashSet<string>(StringComparer.Ordinal);
        document.PinnedPresets.RemoveAll(p =>
            p is null
            || string.IsNullOrWhiteSpace(p.Id)
            || string.IsNullOrWhiteSpace(p.AgentId)
            || string.IsNullOrWhiteSpace(p.BackendRef)
            || !seenPresets.Add(p.Id.Trim()));

        foreach (var preset in document.PinnedPresets)
        {
            preset.Id = preset.Id.Trim();
            preset.AgentId = preset.AgentId.Trim();
            preset.BackendRef = preset.BackendRef.Trim();
            preset.ModelId = string.IsNullOrWhiteSpace(preset.ModelId) ? null : preset.ModelId.Trim();
            preset.Effort = string.IsNullOrWhiteSpace(preset.Effort) ? null : preset.Effort.Trim();
            preset.CustomLabel = string.IsNullOrWhiteSpace(preset.CustomLabel) ? null : preset.CustomLabel.Trim();
        }

        return document;
    }
}
