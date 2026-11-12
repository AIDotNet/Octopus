using System.Text.Json;
using Octopus.Core.Settings;
using Octopus.Platform;

namespace Octopus.Storage;

/// <summary>Stores settings in <c>&lt;DataDirectory&gt;/settings.json</c>. Write failures are recorded in <see cref="LastError"/>, never thrown.</summary>
public sealed class JsonSettingsStore(IAppPaths paths) : ISettingsStore
{
    private readonly string _path = Path.Combine(paths.DataDirectory, "settings.json");
    private readonly object _gate = new();

    public AppSettingsModel Current { get; private set; } = new();

    /// <summary>The last failure to write the file; null when the last write succeeded.</summary>
    public Exception? LastError { get; private set; }

    public event EventHandler? Changed;

    public void Load()
    {
        lock (_gate)
        {
            Current = Read();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Action<AppSettingsModel> mutate)
    {
        lock (_gate)
        {
            mutate(Current);
            Current = SettingsNormalizer.Normalize(Current);
            try
            {
                var text = JsonSerializer.Serialize(Current, OctopusJsonContext.FileOptions.GetTypeInfo<AppSettingsModel>());
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

    private AppSettingsModel Read()
    {
        if (!File.Exists(_path) || !AtomicFile.TryReadAllText(_path, out var text)) return new AppSettingsModel();
        try
        {
            var settings = JsonSerializer.Deserialize(text, OctopusJsonContext.FileOptions.GetTypeInfo<AppSettingsModel>());
            return SettingsNormalizer.Normalize(settings);
        }
        catch (JsonException)
        {
            AtomicFile.Quarantine(_path);
            return new AppSettingsModel();
        }
    }
}
