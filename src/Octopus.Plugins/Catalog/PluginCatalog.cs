using Octopus.Core.Plugins;
using Octopus.Plugins.Manifests;
using Octopus.Storage;

namespace Octopus.Plugins.Catalog;

public enum PluginStatus
{
    /// <summary>Valid, compatible and switched on.</summary>
    Enabled,
    /// <summary>Valid and compatible but switched off (installed and development plugins start off).</summary>
    Disabled,
    /// <summary>The manifest has errors; see the package's issues.</summary>
    Invalid,
    /// <summary>Written for another plugin API or Octopus version.</summary>
    Incompatible,
    /// <summary>Another copy with the same id wins (development over installed over built-in).</summary>
    Shadowed,
}

/// <summary>One plugin package found on disk, with what Octopus makes of it.</summary>
/// <param name="Id">The manifest id; the directory name when the manifest could not be read.</param>
public sealed record PluginPackage(
    string Id,
    string Directory,
    PluginSource Source,
    PluginManifest? Manifest,
    IReadOnlyList<ManifestIssue> Issues,
    PluginStatus Status);

/// <summary>Where to look besides the installed packages: built-in roots shipped with the app, and development directories from settings.</summary>
public sealed class PluginCatalogOptions
{
    /// <summary>Directories whose sub-directories are built-in plugin packages.</summary>
    public IReadOnlyList<string> BuiltinRoots { get; init; } = [];
    /// <summary>Package directories registered for development (each one is a package, not a root).</summary>
    public Func<IReadOnlyList<string>> DevDirectories { get; init; } = () => [];
}

/// <summary>All plugin packages and their status. <see cref="Refresh"/> reads the disk again.</summary>
public interface IPluginCatalog
{
    IReadOnlyList<PluginPackage> Packages { get; }
    event EventHandler? Changed;
    void Refresh();
    /// <summary>The package that counts for an id (never a shadowed copy); null when there is none.</summary>
    PluginPackage? Find(string id);
}

public sealed class PluginCatalog(PluginPaths paths, IPluginStateStore state, PluginHostInfo host, PluginCatalogOptions options) : IPluginCatalog
{
    private readonly object _gate = new();
    private IReadOnlyList<PluginPackage> _packages = [];

    public IReadOnlyList<PluginPackage> Packages => _packages;
    public event EventHandler? Changed;

    public PluginPackage? Find(string id) => _packages.FirstOrDefault(p => p.Id == id && p.Status != PluginStatus.Shadowed);

    public void Refresh()
    {
        var found = new List<(string Directory, PluginSource Source, ManifestReadResult Result)>();
        foreach (var root in options.BuiltinRoots)
        {
            foreach (var directory in PackageDirectories(root))
                found.Add((directory, PluginSource.Builtin, PluginManifestReader.ReadPackage(directory, PluginSource.Builtin, requireDirectoryName: true)));
        }
        foreach (var directory in PackageDirectories(paths.PluginsDirectory))
        {
            found.Add((directory, PluginSource.File, PluginManifestReader.ReadPackage(directory, PluginSource.File, requireDirectoryName: true)));
        }
        foreach (var directory in options.DevDirectories().Where(Directory.Exists).Select(Path.GetFullPath).Distinct())
        {
            found.Add((directory, PluginSource.Dev, PluginManifestReader.ReadPackage(directory, PluginSource.Dev, requireDirectoryName: false)));
        }

        var document = state.Current;
        var packages = new List<PluginPackage>();
        foreach (var group in found.GroupBy(f => f.Result.Manifest?.Id ?? Path.GetFileName(f.Directory), StringComparer.Ordinal))
        {
            // Development beats installed beats built-in; within a source the first found wins.
            var ordered = group.OrderByDescending(f => f.Source).ToList();
            var winner = ordered[0];
            packages.Add(Classify(group.Key, winner.Directory, winner.Source, winner.Result, document));
            foreach (var loser in ordered.Skip(1))
            {
                var issues = loser.Result.Issues.Append(new ManifestIssue(IssueSeverity.Warning, "$",
                    $"not used: the {winner.Source.ToString().ToLowerInvariant()} copy at {winner.Directory} has the same id")).ToList();
                packages.Add(new PluginPackage(group.Key, loser.Directory, loser.Source, loser.Result.Manifest, issues, PluginStatus.Shadowed));
            }
        }
        lock (_gate)
        {
            _packages = packages
                .OrderBy(p => p.Manifest?.Name ?? p.Id, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(p => p.Status == PluginStatus.Shadowed)
                .ToList();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private PluginPackage Classify(string id, string directory, PluginSource source, ManifestReadResult result, PluginsDocument document)
    {
        var issues = result.Issues.ToList();
        if (result.Manifest is not { } manifest || !result.IsValid)
        {
            return new PluginPackage(id, directory, source, result.Manifest, issues, PluginStatus.Invalid);
        }
        if (Incompatibility(manifest) is { } reason)
        {
            issues.Add(new ManifestIssue(IssueSeverity.Error, "$.engines", reason));
            return new PluginPackage(id, directory, source, manifest, issues, PluginStatus.Incompatible);
        }
        var record = document.Find(id);
        // Built-in plugins are on unless switched off; anything else is off until the user enables it (and grants its permissions).
        var enabled = source == PluginSource.Builtin ? record?.Enabled ?? true : record?.Enabled ?? false;
        return new PluginPackage(id, directory, source, manifest, issues, enabled ? PluginStatus.Enabled : PluginStatus.Disabled);
    }

    private string? Incompatibility(PluginManifest manifest)
    {
        if (manifest.Engines.Api > host.ApiVersion)
        {
            return $"needs plugin API {manifest.Engines.Api}; this version of Octopus offers API {host.ApiVersion}. Update Octopus.";
        }
        if (manifest.Engines.Api < PluginApi.MinimumVersion)
        {
            return $"was written for plugin API {manifest.Engines.Api}, which this version of Octopus no longer supports.";
        }
        if (manifest.Engines.Octopus is { } text && VersionRange.TryParse(text, out var range) && !range.Contains(host.AppVersion))
        {
            return $"needs Octopus {text}; this is {host.AppVersion}.";
        }
        return null;
    }

    /// <summary>Sub-directories of a root, skipping hidden ones (staging and backups start with a dot).</summary>
    private static IEnumerable<string> PackageDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }
        try
        {
            return Directory.EnumerateDirectories(root)
                .Where(d => !Path.GetFileName(d).StartsWith('.'))
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
