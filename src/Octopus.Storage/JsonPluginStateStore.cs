using System.Text.Json;
using Octopus.Core.Plugins;
using Octopus.Platform;

namespace Octopus.Storage;

/// <summary>Stores plugin state in <c>&lt;DataDirectory&gt;/plugins.json</c>. Write failures are recorded in <see cref="LastError"/>, never thrown.</summary>
public sealed class JsonPluginStateStore(IAppPaths paths) : IPluginStateStore
{
    /// <summary>How many crash times are kept per plugin (the back-off only looks at the last few minutes).</summary>
    public const int MaxCrashesKept = 10;

    private readonly string _path = Path.Combine(paths.DataDirectory, "plugins.json");
    private readonly object _gate = new();

    public PluginsDocument Current { get; private set; } = new();

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

    public void Update(Action<PluginsDocument> mutate)
    {
        lock (_gate)
        {
            mutate(Current);
            Current = Normalize(Current);
            try
            {
                var text = JsonSerializer.Serialize(Current, OctopusJsonContext.FileOptions.GetTypeInfo<PluginsDocument>());
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

    private PluginsDocument Read()
    {
        if (!File.Exists(_path) || !AtomicFile.TryReadAllText(_path, out var text)) return new PluginsDocument();
        try
        {
            return Normalize(JsonSerializer.Deserialize(text, OctopusJsonContext.FileOptions.GetTypeInfo<PluginsDocument>()));
        }
        catch (JsonException)
        {
            // Kept for inspection; starting empty means no plugin is enabled and nothing is granted.
            AtomicFile.Quarantine(_path);
            return new PluginsDocument();
        }
    }

    /// <summary>Fills in what a hand-edited or older file may lack and drops entries that cannot be right.</summary>
    internal static PluginsDocument Normalize(PluginsDocument? document)
    {
        document ??= new PluginsDocument();
        document.Plugins ??= [];
        foreach (var key in document.Plugins.Keys.Where(k => string.IsNullOrWhiteSpace(k) || document.Plugins[k] is null).ToList())
        {
            document.Plugins.Remove(key);
        }
        foreach (var record in document.Plugins.Values)
        {
            record.Grants ??= [];
            record.PathGrants ??= [];
            record.PathGrants.RemoveAll(g => g is null || string.IsNullOrWhiteSpace(g.Path) || string.IsNullOrWhiteSpace(g.Scope));
            record.Denied ??= [];
            record.Crashes ??= [];
            if (record.Crashes.Count > MaxCrashesKept)
            {
                record.Crashes = record.Crashes.OrderBy(t => t).TakeLast(MaxCrashesKept).ToList();
            }
        }
        return document;
    }
}
