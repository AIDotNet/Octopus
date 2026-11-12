using Octopus.Core.Plugins;
using Octopus.Platform;
using Octopus.Storage;

namespace Octopus.Plugins.Tests;

/// <summary>A fresh data and cache directory per test.</summary>
public sealed class TempAppPaths : IAppPaths, IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("octopus-plugins-").FullName;
    public string DataDirectory => Path.Combine(Root, "data");
    public string CacheDirectory => Path.Combine(Root, "cache");

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}

/// <summary>Writes a plugin package: plugin.json plus any files.</summary>
public static class Package
{
    public static string Write(string directory, string manifest, params (string Path, string Content)[] files)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), manifest);
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(directory, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return directory;
    }

    /// <summary>A minimal valid manifest; <paramref name="extra"/> is spliced into the top-level object.</summary>
    public static string Manifest(string id = "acme.sample", string extra = "", int api = 0) =>
        $$"""{ "schema": 1, "id": "{{id}}", "name": "Sample", "version": "1.0.0", "engines": { "api": {{api}} }{{(extra.Length > 0 ? ", " + extra : "")}} }""";
}

/// <summary>Plugin state kept in memory.</summary>
public sealed class MemoryPluginState : IPluginStateStore
{
    public PluginsDocument Current { get; } = new();
    public event EventHandler? Changed;
    public void Load() => Changed?.Invoke(this, EventArgs.Empty);
    public void Update(Action<PluginsDocument> mutate)
    {
        mutate(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
