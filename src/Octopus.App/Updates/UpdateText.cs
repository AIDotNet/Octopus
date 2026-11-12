using System.Globalization;
using Octopus.App.Localization;
using Octopus.Platform.Updates;
using Octopus.Updates;
using TKCode.Arc.Localization;

namespace Octopus.App.Updates;

/// <summary>The words the card and the settings page share.</summary>
internal static class UpdateText
{
    public static string Error(UpdateSnapshot snapshot) => snapshot.Error switch
    {
        UpdateErrorKind.RateLimited => AppLangResources.Get(AppLangResourceKind.UpdateErrorRateLimited),
        UpdateErrorKind.Integrity => AppLangResources.Get(AppLangResourceKind.UpdateErrorIntegrity),
        UpdateErrorKind.Package => AppLangResources.Format(AppLangResourceKind.UpdateErrorPackage, snapshot.ErrorDetail ?? ""),
        UpdateErrorKind.Disk => AppLangResources.Format(AppLangResourceKind.UpdateErrorDisk, snapshot.ErrorDetail ?? ""),
        UpdateErrorKind.Install => AppLangResources.Format(AppLangResourceKind.UpdateErrorInstall, snapshot.Offer?.Version.ToString() ?? "", snapshot.ErrorDetail ?? ""),
        _ => AppLangResources.Get(AppLangResourceKind.UpdateErrorNetwork),
    };

    /// <summary>Why this install only offers the download page.</summary>
    public static string Unsupported(InstallKind kind) => kind == InstallKind.ReadOnly
        ? AppLangResources.Get(AppLangResourceKind.UpdateReadOnlyDetail)
        : AppLangResources.Get(AppLangResourceKind.UpdatePortableDetail);

    /// <summary>"Preview · released 11/12/2026".</summary>
    public static string Release(UpdateOffer offer)
    {
        var kind = AppLangResources.Get(offer.Version.IsPreview ? AppLangResourceKind.UpdateKindPreview : AppLangResourceKind.UpdateKindRelease);
        return offer.PublishedAt is { } date
            ? kind + " · " + AppLangResources.Format(AppLangResourceKind.UpdatePublished, date.ToLocalTime().ToString("d", ArcLanguage.Culture))
            : kind;
    }

    /// <summary>"38.2 / 61.0 MB".</summary>
    public static string Size(DownloadProgress progress) =>
        AppLangResources.Format(AppLangResourceKind.UpdateProgressSize, Megabytes(progress.Received), Megabytes(progress.Total));

    /// <summary>"4.2 MB/s · about 6 s left"; empty until the speed is known.</summary>
    public static string Speed(DownloadProgress progress)
    {
        var parts = new List<string>();
        if (progress.BytesPerSecond > 0) parts.Add(Megabytes((long)progress.BytesPerSecond) + " MB/s");
        if (progress.Remaining is { } left)
        {
            parts.Add(left < TimeSpan.FromSeconds(90)
                ? AppLangResources.Format(AppLangResourceKind.UpdateRemainingSeconds, Math.Max(1, (int)Math.Ceiling(left.TotalSeconds)))
                : AppLangResources.Format(AppLangResourceKind.UpdateRemainingMinutes, (int)Math.Ceiling(left.TotalMinutes)));
        }
        return string.Join(" · ", parts);
    }

    public static string Percent(DownloadProgress progress) => progress.Fraction.ToString("P0", ArcLanguage.Culture);

    private static string Megabytes(long bytes) => (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture);
}
