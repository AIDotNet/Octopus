using System.Text.Json;
using System.Text.RegularExpressions;
using Octopus.Core.Plugins;

namespace Octopus.Plugins.Manifests;

/// <summary>
/// Reads and checks a plugin's <c>plugin.json</c> (design/plugins/03-清单与生命周期.md §2). Never throws for bad input:
/// every problem becomes a <see cref="ManifestIssue"/> with the JSON path it is about.
/// </summary>
public static partial class PluginManifestReader
{
    public const int MaxIdLength = 128;

    private static readonly string[] KnownTopLevel =
    [
        "$schema", "schema", "id", "name", "version", "description", "publisher", "icon", "homepage", "license", "engines",
        "main", "unsafeEval", "builtin", "activation", "permissions", "contributes", "localized",
    ];

    private static readonly string[] PlainActivations = ["onStartup", "onProject", "onTerminal"];
    private static readonly string[] PrefixActivations = ["onProject:contains:", "onAgent:", "onAgentInstalled:", "onFile:"];

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*(?:\\.[a-z0-9]+(?:-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>Reads <c>plugin.json</c> from a package directory.</summary>
    /// <param name="requireDirectoryName">Installed and built-in packages must live in a directory named after their id; development directories need not.</param>
    public static ManifestReadResult ReadPackage(string packageDirectory, PluginSource source, bool requireDirectoryName)
    {
        var file = Path.Combine(packageDirectory, PluginApi.ManifestFileName);
        var fail = (string message) => new ManifestReadResult(null, [new ManifestIssue(IssueSeverity.Error, "$", message)]);
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                return fail($"{PluginApi.ManifestFileName} not found");
            }
            if (info.Length > PluginApi.MaxManifestBytes)
            {
                return fail($"{PluginApi.ManifestFileName} is larger than {PluginApi.MaxManifestBytes / 1024} KB");
            }
            var text = File.ReadAllText(file);
            var expectedId = requireDirectoryName ? Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageDirectory))) : null;
            return Parse(text, packageDirectory, source, expectedId);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return fail($"{PluginApi.ManifestFileName} cannot be read: {e.Message}");
        }
    }

    /// <summary>Reads manifest text. Without a package directory, referenced files are not checked.</summary>
    public static ManifestReadResult Parse(string json, string? packageDirectory, PluginSource source, string? expectedId = null)
    {
        var log = new IssueLog();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                MaxDepth = 64,
            });
        }
        catch (JsonException e)
        {
            log.Error("$", $"is not valid JSON (line {e.LineNumber + 1}): {e.Message}");
            return new ManifestReadResult(null, log.Issues);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                log.Error("$", "must be a JSON object");
                return new ManifestReadResult(null, log.Issues);
            }
            var manifest = ReadRoot(new ManifestObject(document.RootElement, "$", log), packageDirectory, source, expectedId);
            return new ManifestReadResult(manifest, log.Issues);
        }
    }

    private static PluginManifest? ReadRoot(ManifestObject m, string? packageDirectory, PluginSource source, string? expectedId)
    {
        m.WarnUnknown(KnownTopLevel);

        var schema = m.Integer("schema") ?? PluginApi.ManifestSchema;
        if (schema != PluginApi.ManifestSchema)
        {
            m.Log.Error(m.At("schema"), $"schema {schema} is not supported; this version of Octopus reads schema {PluginApi.ManifestSchema}");
        }

        var id = m.String("id", required: true);
        if (id is not null && !CheckId(m, id, expectedId))
        {
            id = null;
        }
        var name = m.String("name", required: true);
        var version = m.String("version", required: true);
        if (version is not null && !SemanticVersion.TryParse(version, out _))
        {
            m.Log.Error(m.At("version"), $"\"{version}\" is not a semantic version such as 1.2.0");
            version = null;
        }
        var engines = ReadEngines(m);

        var main = m.String("main");
        if (main is not null)
        {
            if (packageDirectory is not null && !PackagePath.TryResolve(packageDirectory, main, out _, out var error))
            {
                m.Log.Error(m.At("main"), $"\"{main}\" {error}");
            }
            else if (!main.EndsWith(".js", StringComparison.Ordinal) && !main.EndsWith(".mjs", StringComparison.Ordinal))
            {
                m.Log.Warn(m.At("main"), "should be an ES module file (.js or .mjs)");
            }
        }

        var icon = m.String("icon");
        // A package image, or one of the app's icons by name (lucide:gauge).
        if (icon is not null && !icon.StartsWith("lucide:", StringComparison.Ordinal) && packageDirectory is not null
            && !PackagePath.TryResolve(packageDirectory, icon, out _, out var iconError))
        {
            m.Log.Warn(m.At("icon"), $"\"{icon}\" {iconError}; the default icon is used");
            icon = null;
        }
        var homepage = m.String("homepage");
        if (homepage is not null && !(Uri.TryCreate(homepage, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"))
        {
            m.Log.Warn(m.At("homepage"), "must be an http or https address; ignored");
            homepage = null;
        }

        var builtin = m.Bool("builtin");
        if (builtin && source != PluginSource.Builtin)
        {
            m.Log.Warn(m.At("builtin"), "only plugins shipped with Octopus can be built-in; ignored");
            builtin = false;
        }
        var unsafeEval = m.Bool("unsafeEval");
        var activation = ReadActivation(m);
        var permissions = PermissionReader.Read(m.Object("permissions"), unsafeEval);
        var contributes = id is null
            ? PluginContributions.Empty
            : new ContributionReader(id, packageDirectory).Read(m.Object("contributes"));
        if (main is null && !contributes.IsDeclarativeOnly)
        {
            m.Log.Error(m.At("main"), "is required: commands, views, editors, panels and providers are implemented by the entry module");
        }

        if (id is null || name is null || version is null || engines is null)
        {
            return null;
        }
        return new PluginManifest
        {
            Schema = (int)schema,
            Id = id,
            Name = name,
            Version = version,
            Description = m.String("description"),
            Publisher = m.String("publisher"),
            Icon = icon,
            Homepage = homepage,
            License = m.String("license"),
            Engines = engines,
            Main = main,
            UnsafeEval = unsafeEval,
            Builtin = builtin,
            Activation = activation,
            Permissions = permissions,
            Contributes = contributes,
            Localized = m.Localized(),
        };
    }

    private static bool CheckId(ManifestObject m, string id, string? expectedId)
    {
        if (id.Length > MaxIdLength || !IdPattern().IsMatch(id))
        {
            m.Log.Error(m.At("id"), $"\"{id}\" must look like publisher.name: lower-case letters, digits and hyphens, at least one dot");
            return false;
        }
        if (expectedId is not null && !string.Equals(id, expectedId, StringComparison.Ordinal))
        {
            m.Log.Error(m.At("id"), $"\"{id}\" must equal the name of the package directory (\"{expectedId}\")");
            return false;
        }
        return true;
    }

    private static PluginEngines? ReadEngines(ManifestObject m)
    {
        if (m.Object("engines", required: true) is not { } engines)
        {
            return null;
        }
        engines.WarnUnknown("api", "octopus");
        var api = engines.Integer("api", required: true, min: 0);
        var range = engines.String("octopus");
        if (range is not null && !VersionRange.TryParse(range, out _))
        {
            engines.Log.Warn(engines.At("octopus"), $"\"{range}\" is not a supported range (use comparators such as \">=0.4.0 <2.0.0\"); ignored");
            range = null;
        }
        return api is null ? null : new PluginEngines((int)Math.Min(api.Value, int.MaxValue), range);
    }

    private static IReadOnlyList<string> ReadActivation(ManifestObject m)
    {
        var events = new List<string>();
        foreach (var activation in m.Strings("activation"))
        {
            var known = PlainActivations.Contains(activation, StringComparer.Ordinal)
                || PrefixActivations.Any(p => activation.StartsWith(p, StringComparison.Ordinal) && activation.Length > p.Length);
            if (known) events.Add(activation);
            else m.Log.Warn(m.At("activation"), $"\"{activation}\" is not an activation event known to this version of Octopus; ignored");
        }
        return events;
    }
}
