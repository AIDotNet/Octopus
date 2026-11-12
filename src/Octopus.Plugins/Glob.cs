using System.Text;
using System.Text.RegularExpressions;

namespace Octopus.Plugins;

/// <summary>
/// File globs as plugins write them: <c>*</c> within a path segment, <c>**</c> across segments, <c>?</c> one character.
/// Matching is against forward-slash relative paths; case-insensitive on macOS and Windows, sensitive on Linux.
/// </summary>
public static class Glob
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Cache = new();

    public static bool IsMatch(string glob, string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        // A pattern without a slash ("*.csv") matches the file name at any depth.
        if (!glob.Contains('/'))
        {
            path = path[(path.LastIndexOf('/') + 1)..];
        }
        return Cache.GetOrAdd(glob, Compile).IsMatch(path);
    }

    private static Regex Compile(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // "**/" matches any number of directories, including none.
                if (i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    sb.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    sb.Append(".*");
                    i++;
                }
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        var options = RegexOptions.CultureInvariant | (OperatingSystem.IsLinux() ? RegexOptions.None : RegexOptions.IgnoreCase);
        return new Regex(sb.ToString(), options, TimeSpan.FromMilliseconds(200));
    }
}
