using System.Globalization;

namespace Octopus.Agents.Signals;

/// <summary>
/// Compares the versions agent CLIs print. They are not all semantic versions (Cursor uses <c>2026.08.11</c>), so
/// only the leading dot-separated numbers count; anything after them (<c>-beta</c>, a commit hash) is ignored.
/// </summary>
public static class AgentVersions
{
    /// <summary>True when <paramref name="installed"/> is at least <paramref name="minimum"/>. An unreadable installed version is not.</summary>
    public static bool IsAtLeast(string? installed, string? minimum)
    {
        if (string.IsNullOrWhiteSpace(minimum))
        {
            return true;
        }
        var have = Parse(installed);
        var need = Parse(minimum);
        if (have.Count == 0)
        {
            return false;
        }
        for (var i = 0; i < Math.Max(have.Count, need.Count); i++)
        {
            var a = i < have.Count ? have[i] : 0;
            var b = i < need.Count ? need[i] : 0;
            if (a != b)
            {
                return a > b;
            }
        }
        return true;
    }

    private static List<long> Parse(string? version)
    {
        var parts = new List<long>();
        if (string.IsNullOrWhiteSpace(version))
        {
            return parts;
        }
        var text = version.Trim().TrimStart('v', 'V');
        foreach (var piece in text.Split('.'))
        {
            var digits = 0;
            while (digits < piece.Length && char.IsAsciiDigit(piece[digits]))
            {
                digits++;
            }
            if (digits == 0 || !long.TryParse(piece.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                break;
            }
            parts.Add(number);
            if (digits < piece.Length)
            {
                break; // "3-beta": the suffix ends the numeric part
            }
        }
        return parts;
    }
}
