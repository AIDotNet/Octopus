namespace Octopus.Core.Agents;

/// <summary>
/// Interface text of one definition element in one language. A null member means "use the default (English) field of
/// the element".
/// </summary>
public sealed class LocalizedText
{
    public string? Label { get; set; }
    public string? Description { get; set; }
    public string? Placeholder { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// Resolves <c>localized</c> dictionaries (keys are BCP 47 tags such as <c>zh-CN</c>): the exact tag first, then the
/// language prefix (<c>zh</c>, also matching <c>zh-TW</c> entries by prefix), and finally the default English field.
/// </summary>
public static class LocalizedTextExtensions
{
    public static LocalizedText? Resolve(this IReadOnlyDictionary<string, LocalizedText>? localized, string? language)
    {
        if (localized is null || localized.Count == 0 || string.IsNullOrWhiteSpace(language))
        {
            return null;
        }
        foreach (var (key, value) in localized)
        {
            if (string.Equals(key, language, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        var prefix = Prefix(language);
        foreach (var (key, value) in localized)
        {
            if (string.Equals(key, prefix, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        foreach (var (key, value) in localized)
        {
            if (string.Equals(Prefix(key), prefix, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }
        return null;
    }

    private static string Prefix(string tag)
    {
        var i = tag.IndexOfAny(['-', '_']);
        return i < 0 ? tag : tag[..i];
    }

    private static string? Pick(IReadOnlyDictionary<string, LocalizedText>? localized, string? language, Func<LocalizedText, string?> member) =>
        localized.Resolve(language) is { } text && member(text) is { Length: > 0 } value ? value : null;

    public static string LabelFor(this AgentFeature feature, string? language) =>
        Pick(feature.Localized, language, t => t.Label) ?? feature.Label;

    public static string? DescriptionFor(this AgentFeature feature, string? language) =>
        Pick(feature.Localized, language, t => t.Description) ?? feature.Description;

    public static string? PlaceholderFor(this AgentFeature feature, string? language) =>
        Pick(feature.Localized, language, t => t.Placeholder) ?? feature.Placeholder;

    public static string LabelFor(this AgentOption option, string? language) =>
        Pick(option.Localized, language, t => t.Label) ?? option.DisplayLabel;

    public static string? NoteFor(this AgentOption option, string? language) =>
        Pick(option.Localized, language, t => t.Note) ?? option.Note;

    public static string TitleFor(this KnownEnvVar variable, string? language) =>
        Pick(variable.Localized, language, t => t.Label) ?? (string.IsNullOrEmpty(variable.Label) ? variable.Name : variable.Label!);

    public static string? DescriptionFor(this KnownEnvVar variable, string? language) =>
        Pick(variable.Localized, language, t => t.Description) ?? variable.Description;

    /// <summary>The mode's own label in the language, or null when the unified label should be used.</summary>
    public static string? LabelFor(this PermissionModeSpec spec, string? language) =>
        Pick(spec.Localized, language, t => t.Label) ?? (string.IsNullOrEmpty(spec.Label) ? null : spec.Label);

    public static string? LabelFor(this EffortCapability effort, string? language) =>
        Pick(effort.Localized, language, t => t.Label) ?? effort.Label;
}
