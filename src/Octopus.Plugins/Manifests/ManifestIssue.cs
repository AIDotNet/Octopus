namespace Octopus.Plugins.Manifests;

public enum IssueSeverity
{
    /// <summary>Something was ignored; the plugin still loads.</summary>
    Warning,
    /// <summary>The plugin cannot be loaded until it is fixed.</summary>
    Error,
}

/// <summary>A problem found in a manifest, with the JSON path of the value (<c>$.contributes.themes[0].path</c>).</summary>
public sealed record ManifestIssue(IssueSeverity Severity, string Path, string Message)
{
    public override string ToString() => $"{(Severity == IssueSeverity.Error ? "error" : "warning")} {Path}: {Message}";
}

/// <summary>What reading a manifest produced. <see cref="Manifest"/> is null only when the basics (id, name, version, engines) could not be read.</summary>
public sealed record ManifestReadResult(Core.Plugins.PluginManifest? Manifest, IReadOnlyList<ManifestIssue> Issues)
{
    public bool IsValid => Manifest is not null && Issues.All(i => i.Severity != IssueSeverity.Error);
    public IEnumerable<ManifestIssue> Errors => Issues.Where(i => i.Severity == IssueSeverity.Error);
}
