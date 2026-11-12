namespace Octopus.Platform;

/// <summary>The only kind of address the app hands to the operating system.</summary>
public static class WebUrl
{
    /// <summary>True for an absolute http or https URL with a host.</summary>
    public static bool IsHttp(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);
}
