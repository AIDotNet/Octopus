using Octopus.App.Localization;

namespace Octopus.App.Common;

internal static class RelativeTime
{
    public static string Format(DateTimeOffset date, DateTimeOffset now)
    {
        var span = now - date;
        if (span < TimeSpan.FromMinutes(1)) return Get(AppLangResourceKind.ExplorerAgoNow, 0);
        if (span < TimeSpan.FromHours(1)) return Get(AppLangResourceKind.ExplorerAgoMinutes, (int)span.TotalMinutes);
        if (span < TimeSpan.FromDays(1)) return Get(AppLangResourceKind.ExplorerAgoHours, (int)span.TotalHours);
        if (span < TimeSpan.FromDays(30)) return Get(AppLangResourceKind.ExplorerAgoDays, (int)span.TotalDays);
        if (span < TimeSpan.FromDays(365)) return Get(AppLangResourceKind.ExplorerAgoMonths, (int)(span.TotalDays / 30));
        return Get(AppLangResourceKind.ExplorerAgoYears, (int)(span.TotalDays / 365));
    }

    private static string Get(AppLangResourceKind kind, int value) =>
        AppLangResources.Format(kind, value);
}
