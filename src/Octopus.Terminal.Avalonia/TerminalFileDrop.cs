using System.Text;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace Octopus.Terminal.Avalonia;

/// <summary>
/// Extracts local paths from Avalonia drag-and-drop payloads and formats them as shell-safe absolute paths
/// for insertion into a terminal.
/// </summary>
public static class TerminalFileDrop
{
    private const string UnixSpecialChars = "'\"\\$`!&|;()<>*?[]{}#~";
    private const string WindowsSpecialChars = "&()^;,=%!`$'\"" + "{}[]#@<>|";

    /// <summary>
    /// Fast, allocation-light check for whether <paramref name="data"/> carries file items. Safe to call on
    /// every <see cref="DragDrop.DragEnterEvent"/> / <see cref="DragDrop.DragOverEvent"/> without touching disk.
    /// </summary>
    public static bool HasFiles(IDataTransfer data)
    {
        if (data.Contains(DataFormat.File)) return true;
        foreach (var item in data.Items)
        {
            if (item.Contains(DataFormat.File)) return true;
        }
        return false;
    }

    /// <summary>
    /// Extracts normalized local file/folder paths from <paramref name="data"/>. Must be called synchronously
    /// inside the drop handler before any <c>await</c>, because the platform disposes the drag payload on return.
    /// </summary>
    public static IReadOnlyList<string> ExtractLocalPaths(IDataTransfer data)
    {
        var items = data.TryGetFiles();
        if (items is null || items.Length == 0) return [];
        var paths = new List<string>(items.Length);
        foreach (var item in items)
        {
            var local = item.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(local)) continue;
            paths.Add(NormalizePath(local));
        }
        return paths;
    }

    /// <summary>Formats one or more paths into a space-separated string suitable for pasting into a shell.</summary>
    public static string FormatPaths(IEnumerable<string> paths, bool? isWindows = null)
    {
        var windows = isWindows ?? OperatingSystem.IsWindows();
        var sb = new StringBuilder();
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(FormatPath(raw, windows));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Formats a single file or folder path for the active shell platform: plain paths stay unquoted, while
    /// paths containing whitespace or shell metacharacters are safely quoted.
    /// </summary>
    public static string FormatPath(string path, bool? isWindows = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var windows = isWindows ?? OperatingSystem.IsWindows();
        var normalized = NormalizePath(path, windows);
        return windows ? QuoteWindows(normalized) : QuoteUnix(normalized);
    }

    private static string NormalizePath(string path, bool? isWindows = null)
    {
        var windows = isWindows ?? OperatingSystem.IsWindows();
        if (windows)
        {
            if (path.Length > 3 && (path[^1] == '\\' || path[^1] == '/') && path[1] == ':')
            {
                return path.TrimEnd('\\', '/');
            }
            if (path.Length > 1 && !path.EndsWith(":\\", StringComparison.Ordinal) && !path.EndsWith(":/", StringComparison.Ordinal))
            {
                var trimmed = path.TrimEnd('\\', '/');
                if (trimmed.Length > 0) return trimmed;
            }
            return path;
        }
        return Path.TrimEndingDirectorySeparator(path);
    }

    private static string QuoteUnix(string path)
    {
        var needsQuoting = false;
        foreach (var c in path)
        {
            if (char.IsWhiteSpace(c) || UnixSpecialChars.Contains(c, StringComparison.Ordinal))
            {
                needsQuoting = true;
                break;
            }
        }
        if (!needsQuoting) return path;
        return $"'{path.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    private static string QuoteWindows(string path)
    {
        var needsQuoting = false;
        foreach (var c in path)
        {
            if (char.IsWhiteSpace(c) || WindowsSpecialChars.Contains(c, StringComparison.Ordinal))
            {
                needsQuoting = true;
                break;
            }
        }
        if (!needsQuoting) return path;
        return $"\"{path.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }
}
