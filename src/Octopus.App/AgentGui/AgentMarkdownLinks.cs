using StreamMark.Avalonia.Extensions;

namespace Octopus.App.AgentGui;

/// <summary>
/// Links to local files in an agent's Markdown replies. Arc's default policy only allows http, https and mailto, so
/// <c>[plan.html](file:///Users/me/plan.html)</c> showed as plain text. Here <c>file:</c> links on this machine (and
/// Windows drive paths) are allowed and, like absolute and relative paths, open in an Octopus tab: never with the
/// system's default application, so a link can't run a script or an app bundle.
/// </summary>
public static class AgentMarkdownLinks
{
    /// <summary>Arc's Markdown defaults with local file links made clickable.</summary>
    public static MarkdownServices Services { get; } = new()
    {
        Plugins = TKCode.Arc.Markdown.DefaultServices.Plugins,
        CodeBlocks = TKCode.Arc.Markdown.DefaultServices.CodeBlocks,
        Links = LinkPolicy.Default with
        {
            // A link to a local file opens in Octopus; its text ("plan.html") never needs to match a host.
            Override = static (url, decision) => IsLocalFileUrl(url.Trim()) || IsDrivePath(url.Trim()) ? LinkDecision.Allow : decision,
        },
    };

    /// <summary>
    /// The local path a clicked link points at: a <c>file:</c> URL on this machine, an absolute path, or a path relative
    /// to <paramref name="workingDirectory"/>. Percent-encoding is decoded. Null for web links and anything else.
    /// </summary>
    public static string? ResolvePath(string url, string? workingDirectory)
    {
        url = url.Trim();
        if (url.Length == 0 || url[0] == '#') return null;
        if (IsLocalFileUrl(url))
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
        }
        if (!IsDrivePath(url) && HasScheme(url)) return null;

        var path = Unescape(StripQueryAndFragment(url));
        if (path.Length == 0) return null;
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        return string.IsNullOrEmpty(workingDirectory) ? null : Path.GetFullPath(Path.Combine(workingDirectory, path));
    }

    private static bool IsLocalFileUrl(string url) =>
        url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile
        && (uri.Host.Length == 0 || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    // C:\Users\me\plan.md or C:/Users/me/plan.md: the drive letter would otherwise read as a URL scheme.
    private static bool IsDrivePath(string url) =>
        url.Length > 2 && char.IsAsciiLetter(url[0]) && url[1] == ':' && url[2] is '\\' or '/';

    // Same rule as Arc's LinkPolicy: a colon before any '/', '?' or '#' starts a scheme.
    private static bool HasScheme(string url)
    {
        var colon = url.IndexOf(':');
        var slash = url.IndexOfAny(['/', '?', '#']);
        return colon >= 0 && (slash < 0 || colon < slash);
    }

    private static string StripQueryAndFragment(string url)
    {
        var end = url.IndexOfAny(['?', '#']);
        return end >= 0 ? url[..end] : url;
    }

    private static string Unescape(string path)
    {
        try { return Uri.UnescapeDataString(path); }
        catch (UriFormatException) { return path; }
    }
}
