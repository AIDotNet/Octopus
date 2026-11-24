using System.Text.RegularExpressions;

namespace Octopus.Acp.Process;

/// <summary>
/// Resolves an npm <c>.cmd</c>/<c>.bat</c> shim (e.g. <c>claude.cmd</c>) to the file it runs. Node adapters spawn the
/// path in <c>CLAUDE_CODE_EXECUTABLE</c> without a shell, and since CVE-2024-27980 Node refuses batch files that way
/// (<c>spawn EINVAL</c>), so they must get the <c>.exe</c> or <c>.js</c> behind the shim instead.
/// </summary>
public static partial class BatchShimResolver
{
    public static bool IsBatchFile(string? path) =>
        path is not null
        && (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the shim's target when <paramref name="path"/> is a batch file that can be resolved; otherwise <paramref name="path"/>.</summary>
    public static string Resolve(string path)
    {
        if (!IsBatchFile(path)) return path;
        try
        {
            var dir = Path.GetDirectoryName(path) ?? "";
            if (File.Exists(path) && ParseTarget(File.ReadAllText(path), dir) is { } target && File.Exists(target))
            {
                return target;
            }
            // A native install next to the shim (claude.exe beside claude.cmd).
            var sibling = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".exe");
            if (File.Exists(sibling)) return sibling;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return path;
    }

    /// <summary>Extracts the program an npm cmd-shim runs: the last <c>"%dp0%\…"</c> path that is not <c>node.exe</c>.</summary>
    public static string? ParseTarget(string shimText, string shimDirectory)
    {
        string? last = null;
        foreach (Match m in Dp0PathRegex().Matches(shimText))
        {
            var rel = m.Groups[1].Value.TrimStart('\\', '/');
            if (rel.Length == 0 || rel.Equals("node.exe", StringComparison.OrdinalIgnoreCase) || rel.Equals("node", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            last = rel;
        }
        if (last is null) return null;
        var parts = last.Split('\\', '/', StringSplitOptions.RemoveEmptyEntries);
        return Path.GetFullPath(Path.Combine([shimDirectory, .. parts]));
    }

    [GeneratedRegex(@"""%~?dp0%?([^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex Dp0PathRegex();
}
