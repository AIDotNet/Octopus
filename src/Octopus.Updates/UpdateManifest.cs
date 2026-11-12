using System.Text.Json;
using System.Text.Json.Serialization;
using Octopus.Core.Updates;

namespace Octopus.Updates;

/// <summary>
/// <c>update.json</c>, published with every release and signed (design/10-自动更新.md §5.1). The channel is not in it:
/// a version with <c>-preview.N</c> is a preview.
/// </summary>
public sealed class UpdateManifest
{
    public int Schema { get; set; } = 1;
    public string Version { get; set; } = "";
    public DateTimeOffset? PublishedAt { get; set; }
    /// <summary>The release page: its notes, and the downloads for an install that cannot update itself.</summary>
    public string? NotesUrl { get; set; }
    /// <summary>Keyed by target: <c>win-x64</c>, <c>osx-arm64</c>, <c>osx-x64</c>, <c>linux-x64</c>.</summary>
    public Dictionary<string, UpdateAsset> Assets { get; set; } = [];
}

/// <summary>One downloadable package of a release.</summary>
public sealed class UpdateAsset
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public long Size { get; set; }
    /// <summary>Lowercase hex.</summary>
    public string Sha256 { get; set; } = "";
}

/// <summary>A newer release found for this install.</summary>
/// <param name="Asset">The package for this platform; null when the release has none (only its page can be offered).</param>
public sealed record UpdateOffer(AppVersion Version, UpdateManifest Manifest, UpdateAsset? Asset)
{
    public string? NotesUrl => Manifest.NotesUrl;
    public DateTimeOffset? PublishedAt => Manifest.PublishedAt;
}

/// <summary>A downloaded, verified and prepared update waiting for the app to quit (<c>updates/pending.json</c>).</summary>
public sealed class PendingUpdate
{
    public string Version { get; set; } = "";
    /// <summary>The downloaded package.</summary>
    public string Package { get; set; } = "";
    /// <summary>What the installer installs from (the package itself, or the app unpacked from it).</summary>
    public string Prepared { get; set; } = "";
    public string? NotesUrl { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    /// <summary>
    /// Downloaded because the person asked ("download and update"); false for a download auto mode started, which is
    /// then only installed while auto mode is on.
    /// </summary>
    public bool Approved { get; set; }
    /// <summary>True once the helper was started; the next start then reads its outcome.</summary>
    public bool Launched { get; set; }
    /// <summary>How many times the helper ran without the version changing.</summary>
    public int Failures { get; set; }
}

/// <summary><c>updates/state.json</c>: when the last check ran. Not in settings.json, which holds choices only.</summary>
public sealed class UpdateCheckState
{
    public DateTimeOffset? LastCheckedAt { get; set; }
}

/// <summary>The part of GitHub's "list releases" answer the feed reads.</summary>
public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("assets")] public List<GitHubReleaseAsset> Assets { get; set; } = [];
}

public sealed class GitHubReleaseAsset
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(PendingUpdate))]
[JsonSerializable(typeof(UpdateCheckState))]
[JsonSerializable(typeof(List<GitHubRelease>))]
internal sealed partial class UpdatesJsonContext : JsonSerializerContext;
