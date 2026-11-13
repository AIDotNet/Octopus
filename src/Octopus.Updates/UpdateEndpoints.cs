namespace Octopus.Updates;

/// <summary>Where releases are published and who may sign their manifests (design/10-自动更新.md §5).</summary>
public static class UpdateEndpoints
{
    public const string ReleasesUrl = GitHubReleasesFeed.DefaultReleasesUrl;

    /// <summary>The project's home on GitHub.</summary>
    public const string RepositoryUrl = "https://github.com/AIDotNet/Octopus";

    /// <summary>The page of one release: its notes and its downloads.</summary>
    public static string ReleasePage(Octopus.Core.Updates.AppVersion version) =>
        $"{RepositoryUrl}/releases/tag/v{version}";

    /// <summary>
    /// The public keys whose signature on <c>update.json</c> is trusted (SubjectPublicKeyInfo, base64). The private key
    /// is the repository secret UPDATE_SIGNING_KEY; tools/make-update-key.sh makes a new pair. Keep the old key here for
    /// at least one release when replacing it.
    /// </summary>
    public static readonly IReadOnlyList<string> PublicKeys =
    [
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEvcHmyRA1T8oyXJJwiXe8yPVQ7jq0QWjvwQu13/+X8NfuRGaTyWB74CcycNg4eW/A7RqMoeyb56e6aUUKpTtlDQ==",
    ];
}
