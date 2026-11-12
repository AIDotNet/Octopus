using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Octopus.Core.Updates;

/// <summary>
/// A release version: <c>major.minor.build</c>, optionally <c>-preview.N</c> (design/10-自动更新.md §3). Compared as
/// SemVer: number by number, then a preview below the release with the same numbers, then previews by <c>N</c>.
/// A leading <c>v</c> (a tag) and <c>+metadata</c> (the commit the SDK appends to the informational version) are accepted
/// and dropped.
/// </summary>
public sealed partial class AppVersion : IComparable<AppVersion>, IEquatable<AppVersion>
{
    public AppVersion(int major, int minor, int build, int? preview = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(major);
        ArgumentOutOfRangeException.ThrowIfNegative(minor);
        ArgumentOutOfRangeException.ThrowIfNegative(build);
        if (preview is < 0) throw new ArgumentOutOfRangeException(nameof(preview));
        Major = major;
        Minor = minor;
        Build = build;
        Preview = preview;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Build { get; }
    /// <summary>The <c>N</c> of <c>-preview.N</c>; null for a release.</summary>
    public int? Preview { get; }
    public bool IsPreview => Preview is not null;

    public static AppVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"Not a version: {text}");

    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out AppVersion? version)
    {
        version = null;
        if (text is null) return false;
        var match = Pattern().Match(text.Trim());
        if (!match.Success
            || !TryNumber(match.Groups["major"].Value, out var major)
            || !TryNumber(match.Groups["minor"].Value, out var minor)
            || !TryNumber(match.Groups["build"].Value, out var build))
        {
            return false;
        }
        int? preview = null;
        if (match.Groups["preview"].Success)
        {
            if (!TryNumber(match.Groups["preview"].Value, out var n)) return false;
            preview = n;
        }
        version = new AppVersion(major, minor, build, preview);
        return true;
    }

    private static bool TryNumber(string text, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Build.CompareTo(other.Build);
        if (c != 0) return c;
        return (Preview, other.Preview) switch
        {
            (null, null) => 0,
            (null, _) => 1,   // a release is above its previews
            (_, null) => -1,
            ({ } a, { } b) => a.CompareTo(b),
        };
    }

    public bool Equals(AppVersion? other) => CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is AppVersion other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Build, Preview);

    public static bool operator ==(AppVersion? a, AppVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(AppVersion? a, AppVersion? b) => !(a == b);
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() =>
        Preview is { } n
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Build}-preview.{n}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Build}");

    [GeneratedRegex(@"^v?(?<major>\d{1,9})\.(?<minor>\d{1,9})\.(?<build>\d{1,9})(?:-preview\.(?<preview>\d{1,9}))?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
