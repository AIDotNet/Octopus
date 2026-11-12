using Octopus.App.Composition;
using Octopus.Storage;
using TKCode.Arc.Localization;

namespace Octopus.App.Localization;

/// <summary>The languages the app has text for (the .xlf files next to this one) and the one it starts in.</summary>
public static class AppLanguages
{
    public static IReadOnlyList<(LanguageTag Tag, string Name)> All { get; } =
    [
        (LanguageTags.ZhCN, "简体中文"),
        (LanguageTags.EnUS, "English"),
    ];

    public static IEnumerable<LanguageTag> Supported => All.Select(language => language.Tag);

    /// <summary>The language chosen in settings, or Simplified Chinese.</summary>
    public static LanguageTag Initial
    {
        get
        {
            var saved = AppHost.Get<ISettingsStore>().Current.Appearance.Language;
            return LanguageTag.TryParse(saved, out var tag) && Supported.Contains(tag) ? tag : LanguageTags.ZhCN;
        }
    }
}
