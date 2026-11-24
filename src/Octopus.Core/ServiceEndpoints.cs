using System.Reflection;

namespace Octopus.Core;

/// <summary>Official Octopus service address compiled into the build via <c>OctopusServiceUrl</c> (Directory.Build.props).</summary>
public static class ServiceEndpoints
{
    public const string DefaultOfficialServiceUrl = "https://octoterm.si";

    /// <summary>
    /// The official Octopus service origin (e.g. <c>https://octoterm.si</c>), without a trailing slash;
    /// null only when the build explicitly cleared <c>OctopusServiceUrl</c>.
    /// </summary>
    public static string? OfficialServiceUrl { get; } = Normalize(
        typeof(ServiceEndpoints).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "OctopusServiceUrl")?.Value);

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim().TrimEnd('/');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
