namespace Octopus.Plugins.Security;

/// <summary>
/// Step 1 of the path check (design/plugins/04 §3.4): forms refused before anything touches the disk. Pure string
/// rules, so the Windows rules are tested on every platform.
/// </summary>
public static class PathSyntax
{
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    ];

    /// <summary>
    /// Null when <paramref name="path"/> may go on to normalization, else why not. <paramref name="isUnc"/> tells the
    /// caller it is a network path (<c>\\server\share\…</c>), which only an explicit <c>external</c> scope allows.
    /// </summary>
    public static string? Check(string path, bool windows, out bool isUnc)
    {
        isUnc = false;
        if (string.IsNullOrEmpty(path)) return "The path is empty.";
        if (path.Contains('\0')) return "The path contains a NUL character.";
        if (!windows) return path.StartsWith('/') ? null : "The path must be absolute.";

        var unified = path.Replace('/', '\\');
        // \\.\ and \\?\ reach devices and skip Win32 normalization; \??\ is the NT object namespace.
        if (unified.StartsWith(@"\\.\", StringComparison.Ordinal) || unified.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            unified.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return "Device and extended-length paths are not allowed.";
        }
        string rest;
        if (unified.StartsWith(@"\\", StringComparison.Ordinal))
        {
            isUnc = true;
            rest = unified[2..];
        }
        else if (unified.Length >= 3 && char.IsAsciiLetter(unified[0]) && unified[1] == ':' && unified[2] == '\\')
        {
            rest = unified[3..];
        }
        else
        {
            return "The path must be absolute, with a drive letter.";
        }
        // A colon after the drive names an alternate data stream (file.txt:secret).
        if (rest.Contains(':')) return "Alternate data streams are not allowed.";
        foreach (var segment in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..") continue;
            if (segment.AsSpan().IndexOfAny("<>\"|?*") >= 0 || segment.Any(char.IsControl)) return $"'{segment}' contains characters Windows does not allow in names.";
            // Windows drops trailing dots and spaces: ".env." would open ".env" past a name check.
            if (segment.EndsWith('.') || segment.EndsWith(' ')) return $"'{segment}' ends in a dot or space.";
            var stem = segment.Split('.')[0].TrimEnd(' ');
            if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) return $"'{segment}' is a reserved device name.";
            // 8.3 short names (PROGRA~1, GIT~1) are other spellings of long names and would slip past name rules.
            if (LooksLikeShortName(segment)) return $"'{segment}' looks like an 8.3 short name; use the long name.";
        }
        return null;
    }

    private static bool LooksLikeShortName(string segment)
    {
        var tilde = segment.IndexOf('~');
        return tilde > 0 && tilde + 1 < segment.Length && char.IsAsciiDigit(segment[tilde + 1]) && segment.Split('.')[0].Length <= 8;
    }
}
