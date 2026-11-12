namespace Octopus.Core.Plugins;

/// <summary>
/// The content of a plugin's <c>plugin.json</c> after validation (design/plugins/03-清单与生命周期.md §2).
/// Paths in it are relative to the package directory and were checked to stay inside it.
/// </summary>
public sealed class PluginManifest
{
    public int Schema { get; init; } = 1;
    /// <summary><c>publisher.name</c>: lower-case letters, digits and hyphens, at least one dot. Equals the package directory name.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string? Description { get; init; }
    public string? Publisher { get; init; }
    public string? Icon { get; init; }
    public string? Homepage { get; init; }
    public string? License { get; init; }
    public required PluginEngines Engines { get; init; }
    /// <summary>The entry ES module; null for a plugin that only declares data (themes, agent definitions…).</summary>
    public string? Main { get; init; }
    /// <summary>Asks for the Standard engine profile (eval and Function allowed). A high-risk permission.</summary>
    public bool UnsafeEval { get; init; }
    /// <summary>Set only by plugins that ship inside Octopus; ignored elsewhere.</summary>
    public bool Builtin { get; init; }
    /// <summary>Explicit activation events. Contribution-derived events are not listed here.</summary>
    public IReadOnlyList<string> Activation { get; init; } = [];
    public PluginPermissions Permissions { get; init; } = PluginPermissions.None;
    public PluginContributions Contributes { get; init; } = PluginContributions.Empty;
    public PluginLocalized Localized { get; init; } = PluginLocalized.Empty;

    public string NameFor(string? language) => Localized.Pick(language, "name") ?? Name;
    public string? DescriptionFor(string? language) => Localized.Pick(language, "description") ?? Description;
}

/// <summary>Which versions of the plugin API and of Octopus the plugin works with.</summary>
/// <param name="Api">Major version of the plugin API the plugin was written for.</param>
/// <param name="Octopus">Optional version range of the app, e.g. <c>&gt;=0.4.0</c>.</param>
public sealed record PluginEngines(int Api, string? Octopus);

/// <summary>Where a plugin package was found. Also decides which copy wins when two share an id: dev, then file, then builtin.</summary>
public enum PluginSource
{
    Builtin,
    File,
    Dev,
}

/// <summary>
/// Interface text of a manifest element in other languages: <c>{"zh-CN": {"name": "…", "description": "…"}}</c>.
/// Keys are BCP 47 tags; fields are free-form names such as <c>name</c>, <c>title</c>, <c>label</c>, <c>reason</c>.
/// </summary>
public sealed class PluginLocalized
{
    public static readonly PluginLocalized Empty = new(new Dictionary<string, IReadOnlyDictionary<string, string>>());

    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _byLanguage;

    public PluginLocalized(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> byLanguage) => _byLanguage = byLanguage;

    public IEnumerable<string> Languages => _byLanguage.Keys;

    /// <summary>
    /// The field in the closest language: the exact tag, then the language prefix (<c>zh</c>), then any tag with the
    /// same prefix (<c>zh-TW</c> for <c>zh-CN</c>); null when none has it, so the caller falls back to the default text.
    /// Same rules as the agent definitions' <c>localized</c>.
    /// </summary>
    public string? Pick(string? language, string field)
    {
        if (_byLanguage.Count == 0 || string.IsNullOrWhiteSpace(language))
        {
            return null;
        }
        var prefix = Prefix(language);
        return Find(key => string.Equals(key, language, StringComparison.OrdinalIgnoreCase))
            ?? Find(key => string.Equals(key, prefix, StringComparison.OrdinalIgnoreCase))
            ?? Find(key => string.Equals(Prefix(key), prefix, StringComparison.OrdinalIgnoreCase));

        string? Find(Func<string, bool> matches)
        {
            foreach (var (key, fields) in _byLanguage)
            {
                if (matches(key) && fields.TryGetValue(field, out var value) && value.Length > 0)
                {
                    return value;
                }
            }
            return null;
        }
    }

    private static string Prefix(string tag)
    {
        var i = tag.IndexOfAny(['-', '_']);
        return i < 0 ? tag : tag[..i];
    }
}
