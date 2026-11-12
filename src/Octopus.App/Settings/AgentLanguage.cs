using TKCode.Arc.Localization;

namespace Octopus.App.Settings;

/// <summary>The language tag (e.g. <c>zh-CN</c>) used to pick <c>localized</c> text of agent definitions.</summary>
public static class AgentLanguage
{
    public static string Current => ArcLanguage.Current.ToString();
}
