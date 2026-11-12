using System.Globalization;

namespace Octopus.Plugins;

/// <summary>A Semantic Versioning 2.0 version: <c>MAJOR.MINOR.PATCH[-pre.release][+build]</c>. Build metadata is ignored when comparing.</summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? Prerelease = null) : IComparable<SemanticVersion>
{
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var core = text.Trim();
        var plus = core.IndexOf('+');
        if (plus >= 0)
        {
            if (!ValidIdentifiers(core[(plus + 1)..], numericNoLeadingZero: false)) return false;
            core = core[..plus];
        }
        string? prerelease = null;
        var dash = core.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = core[(dash + 1)..];
            if (!ValidIdentifiers(prerelease, numericNoLeadingZero: true)) return false;
            core = core[..dash];
        }
        var parts = core.Split('.');
        if (parts.Length != 3 || !Number(parts[0], out var major) || !Number(parts[1], out var minor) || !Number(parts[2], out var patch))
        {
            return false;
        }
        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        // A pre-release sorts before the release.
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;
        var a = Prerelease.Split('.');
        var b = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNum = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var x);
            var bNum = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var y);
            c = (aNum, bNum) switch
            {
                (true, true) => x.CompareTo(y),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";

    private static bool Number(string s, out int value) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value) && (s == "0" || s[0] != '0');

    private static bool ValidIdentifiers(string s, bool numericNoLeadingZero) =>
        s.Length > 0 && s.Split('.').All(id =>
            id.Length > 0
            && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')
            && (!numericNoLeadingZero || !id.All(char.IsAsciiDigit) || id == "0" || id[0] != '0'));
}

/// <summary>
/// A version range for <c>engines.octopus</c>: space-separated comparators (<c>&gt;=0.4.0 &lt;2.0.0</c>), all of which must
/// hold. Supported operators: <c>&gt;=</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&lt;</c>, <c>=</c> and a bare version.
/// </summary>
public sealed class VersionRange
{
    private readonly List<(string Op, SemanticVersion Version)> _comparators;

    private VersionRange(List<(string, SemanticVersion)> comparators) => _comparators = comparators;

    public static bool TryParse(string? text, out VersionRange range)
    {
        range = new VersionRange([]);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var list = new List<(string, SemanticVersion)>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var op = token.StartsWith(">=", StringComparison.Ordinal) ? ">="
                : token.StartsWith("<=", StringComparison.Ordinal) ? "<="
                : token.StartsWith('>') ? ">"
                : token.StartsWith('<') ? "<"
                : token.StartsWith('=') ? "="
                : "";
            if (!SemanticVersion.TryParse(token[op.Length..], out var version))
            {
                return false;
            }
            list.Add((op.Length == 0 ? "=" : op, version));
        }
        range = new VersionRange(list);
        return list.Count > 0;
    }

    public bool Contains(SemanticVersion version) => _comparators.All(c => c.Op switch
    {
        ">=" => version >= c.Version,
        ">" => version > c.Version,
        "<=" => version <= c.Version,
        "<" => version < c.Version,
        _ => version.CompareTo(c.Version) == 0,
    });
}
