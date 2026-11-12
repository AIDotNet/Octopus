using System.Collections.Concurrent;
using Jaunt;
using Octopus.Plugins.Manifests;
using Octopus.Plugins.Runtime;

namespace Octopus.Plugins.Jaunt;

/// <summary>
/// Resolves imports of a plugin (design/plugins/02-运行时.md §4): <c>octopus</c> is the SDK; relative specifiers are
/// files of the plugin's own package and must stay inside it; anything else (npm names, <c>node:</c>) is refused —
/// plugins bundle their dependencies.
/// </summary>
internal sealed class PluginModuleResolver(string pluginId, string packageDirectory) : IJsModuleResolver
{
    public const int MaxSourceBytes = 4 * 1024 * 1024;
    public const int MaxBytesModule = 4 * 1024 * 1024;

    private static readonly Lazy<JsModule> Sdk = new(() =>
        JsCompiler.CompileModule(PluginSdk.Source, new JsCompileOptions { SourceName = PluginSdk.ModuleId }));

    /// <summary>Compiled modules by file and its size and time: plugin restarts and second engines skip compiling.</summary>
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Written, string Kind), JsModule> Cache = new();

    public static JsModule SdkModule => Sdk.Value;

    public string IdOf(string relativePath) => $"plugin://{pluginId}/{relativePath.Replace('\\', '/').TrimStart('/')}";

    public JsModuleSource? Resolve(string specifier, string? referrerId) =>
        Resolve(specifier, referrerId, new Dictionary<string, string>());

    public JsModuleSource? Resolve(string specifier, string? referrerId, IReadOnlyDictionary<string, string> attributes)
    {
        if (specifier == PluginSdk.ModuleId)
        {
            return new JsModuleSource(PluginSdk.ModuleId, Sdk.Value);
        }
        if (!specifier.StartsWith("./", StringComparison.Ordinal) && !specifier.StartsWith("../", StringComparison.Ordinal))
        {
            return null;
        }
        var prefix = $"plugin://{pluginId}/";
        var referrerPath = referrerId is not null && referrerId.StartsWith(prefix, StringComparison.Ordinal) ? referrerId[prefix.Length..] : "";
        var relative = Combine(referrerPath, specifier);
        if (relative is null || !PackagePath.TryResolve(packageDirectory, relative, out var full, out _))
        {
            return null;
        }
        attributes.TryGetValue("type", out var type);
        return new JsModuleSource(IdOf(relative), Load(full, IdOf(relative), type ?? "js"));
    }

    public JsModule LoadEntry(string relativePath)
    {
        if (!PackagePath.TryResolve(packageDirectory, relativePath, out var full, out var error))
        {
            throw new PluginLoadException($"The entry module \"{relativePath}\" {error}.");
        }
        return Load(full, IdOf(relativePath), "js");
    }

    private static JsModule Load(string full, string id, string type)
    {
        var info = new FileInfo(full);
        var limit = type == "bytes" ? MaxBytesModule : MaxSourceBytes;
        if (info.Length > limit)
        {
            throw new PluginLoadException($"{id} is larger than {limit / 1024 / 1024} MB.", id);
        }
        return Cache.GetOrAdd((full, info.Length, info.LastWriteTimeUtc, type), _ =>
        {
            var options = new JsCompileOptions { SourceName = id, MaxSourceLength = MaxSourceBytes, Timeout = TimeSpan.FromSeconds(5) };
            return type switch
            {
                "json" => JsCompiler.CompileJsonModule(File.ReadAllText(full), options),
                "text" => JsCompiler.CreateTextModule(File.ReadAllText(full), options),
                "bytes" => JsCompiler.CreateBytesModule(File.ReadAllBytes(full), options),
                _ => JsCompiler.CompileModule(File.ReadAllText(full), options),
            };
        });
    }

    /// <summary>Joins a relative specifier to the referrer's directory; null when it climbs above the package root.</summary>
    private static string? Combine(string referrerPath, string specifier)
    {
        var parts = new List<string>(referrerPath.Split('/', StringSplitOptions.RemoveEmptyEntries));
        if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
        foreach (var segment in specifier.Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }
}
