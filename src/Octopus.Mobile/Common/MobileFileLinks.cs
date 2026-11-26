using System.Buffers.Binary;

namespace Octopus.Mobile.Common;

/// <summary>
/// Resolves Markdown links, image sources, and agent tool paths into remote host file paths, and recognizes previewable
/// image and Markdown files on mobile without desktop-only dependencies.
/// </summary>
public static class MobileFileLinks
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".apng", ".jpg", ".jpeg", ".jpe", ".jfif", ".gif", ".webp", ".bmp", ".ico",
    };

    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdx",
    };

    public static bool IsWebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var trimmed = url.Trim();
        return (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && uri.Scheme is "https" or "http";
    }

    public static bool IsImageExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(StripLocationSuffix(path));
        return ext.Length > 0 && ImageExtensions.Contains(ext);
    }

    public static bool IsMarkdownExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(StripLocationSuffix(path));
        return ext.Length > 0 && MarkdownExtensions.Contains(ext);
    }

    /// <summary>
    /// Sniffs the raster image format from header bytes (PNG, JPEG, GIF, WebP, BMP, ICO); returns null when the bytes
    /// are not a recognized image.
    /// </summary>
    public static string? SniffImageFormat(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 8 && h[0] == 0x89 && h[1] == 'P' && h[2] == 'N' && h[3] == 'G' && h[4] == 0x0D && h[5] == 0x0A && h[6] == 0x1A && h[7] == 0x0A) return "PNG";
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return "JPEG";
        if (h.Length >= 6 && h[0] == 'G' && h[1] == 'I' && h[2] == 'F' && h[3] == '8' && (h[4] == '7' || h[4] == '9') && h[5] == 'a') return "GIF";
        if (h.Length >= 12 && h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P') return "WebP";
        if (h.Length >= 18 && h[0] == 'B' && h[1] == 'M' && BinaryPrimitives.ReadUInt32LittleEndian(h[14..]) is 12 or 40 or 52 or 56 or 64 or 108 or 124) return "BMP";
        if (h.Length >= 6 && h[0] == 0 && h[1] == 0 && h[2] == 1 && h[3] == 0 && h[5] == 0 && h[4] is >= 1 and <= 64) return "ICO";
        return null;
    }

    /// <summary>
    /// Resolves a clicked Markdown link, image path, or tool file path into a normalized remote host path relative to
    /// <paramref name="workingDirectory"/>. Supports <c>file://</c> URLs, absolute POSIX/Windows paths, and relative
    /// paths with optional <c>#L10-L20</c> or <c>:10:5</c> suffixes. Returns null for web URLs, anchors, and unsafe schemes.
    /// </summary>
    public static string? ResolveRemotePath(string? rawTarget, string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(rawTarget)) return null;
        var target = rawTarget.Trim();
        if (target.Length == 0 || target[0] == '#') return null;
        if (IsWebUrl(target)) return null;

        if (target.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || !uri.IsFile) return null;
            if (uri.Host.Length > 0 && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return null;
            var local = StripLineSuffix(uri.LocalPath);
            return local.Length == 0 ? null : NormalizeHostPath(local, preferWindows: IsWindowsPath(local) || IsWindowsPath(workingDirectory));
        }

        if (!IsDrivePath(target) && HasNonPathScheme(target)) return null;

        var cleaned = Unescape(StripLocationSuffix(target)).Trim();
        if (cleaned.Length == 0) return null;

        var isWindows = IsWindowsPath(cleaned) || IsWindowsPath(workingDirectory);
        if (IsAbsoluteHostPath(cleaned))
        {
            return NormalizeHostPath(cleaned, preferWindows: isWindows);
        }

        if (string.IsNullOrWhiteSpace(workingDirectory)) return null;
        var sep = isWindows ? '\\' : '/';
        var combined = workingDirectory.TrimEnd('/', '\\') + sep + cleaned;
        return NormalizeHostPath(combined, preferWindows: isWindows);
    }

    /// <summary>Returns the parent folder of a remote host path (keeping host separators).</summary>
    public static string? ParentDirectoryOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(['/', '\\']);
        if (cut < 0) return null;
        if (cut == 0) return trimmed[..1];
        return trimmed[..cut];
    }

    private static string StripLocationSuffix(string path)
    {
        var cut = path.IndexOfAny(['?', '#']);
        var withoutFragment = cut >= 0 ? path[..cut] : path;
        return StripLineSuffix(withoutFragment);
    }

    // Strips trailing ":12" or ":12:5" line/column markers without touching Windows drive colons (C:\...).
    private static string StripLineSuffix(string path)
    {
        var end = path.Length;
        for (var pass = 0; pass < 2; pass++)
        {
            var colon = path.LastIndexOf(':', end - 1);
            if (colon <= 1 || colon == end - 1) break;
            var allDigits = true;
            for (var i = colon + 1; i < end; i++)
            {
                if (!char.IsAsciiDigit(path[i]))
                {
                    allDigits = false;
                    break;
                }
            }
            if (!allDigits) break;
            end = colon;
        }
        return path[..end];
    }

    private static bool IsDrivePath(string value) =>
        value.Length > 2 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/';

    private static bool IsWindowsPath(string? value) =>
        !string.IsNullOrEmpty(value) && (IsDrivePath(value) || value.StartsWith("\\\\", StringComparison.Ordinal));

    private static bool IsAbsoluteHostPath(string value) =>
        value.StartsWith('/') || value.StartsWith("~/") || value.StartsWith("~\\") || IsDrivePath(value) || value.StartsWith("\\\\", StringComparison.Ordinal);

    private static bool HasNonPathScheme(string url)
    {
        var colon = url.IndexOf(':');
        if (colon < 0) return false;
        var slash = url.IndexOfAny(['/', '\\', '?', '#']);
        if (slash >= 0 && colon > slash) return false;
        var before = url[..colon];
        var afterIsLine = colon + 1 < url.Length && char.IsAsciiDigit(url[colon + 1]);
        return !(afterIsLine && before.Contains('.'));
    }

    private static string NormalizeHostPath(string path, bool preferWindows)
    {
        var sep = preferWindows ? '\\' : '/';
        var normalized = path.Replace(preferWindows ? '/' : '\\', sep);

        string prefix;
        string rest;
        if (preferWindows && normalized.Length >= 3 && char.IsAsciiLetter(normalized[0]) && normalized[1] == ':' && normalized[2] == '\\')
        {
            prefix = normalized[..3];
            rest = normalized[3..];
        }
        else if (preferWindows && normalized.StartsWith("\\\\", StringComparison.Ordinal))
        {
            prefix = "\\\\";
            rest = normalized[2..];
        }
        else if (normalized.StartsWith("~/", StringComparison.Ordinal) || normalized.StartsWith("~\\", StringComparison.Ordinal))
        {
            prefix = "~" + sep;
            rest = normalized[2..];
        }
        else if (normalized.StartsWith(sep))
        {
            prefix = sep.ToString();
            rest = normalized[1..];
        }
        else
        {
            prefix = "";
            rest = normalized;
        }

        var parts = new List<string>();
        foreach (var part in rest.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0 && parts[^1] != "..") parts.RemoveAt(parts.Count - 1);
                else if (prefix.Length == 0) parts.Add("..");
                continue;
            }
            parts.Add(part);
        }
        return prefix + string.Join(sep, parts);
    }

    private static string Unescape(string path)
    {
        try { return Uri.UnescapeDataString(path); }
        catch (UriFormatException) { return path; }
    }
}
