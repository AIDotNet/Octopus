namespace Octopus.App.Common;

/// <summary>
/// Compares paths the way the file system of the platform does: ignoring a trailing separator, and ignoring case where
/// the platform's file system does. Which one applies is decided once, in <c>PlatformRegistration</c>.
/// </summary>
public sealed class PathComparer(bool ignoreCase) : IEqualityComparer<string>
{
    private readonly StringComparison _comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public bool Equals(string? x, string? y) =>
        x is null || y is null ? x is null && y is null : string.Equals(Trim(x), Trim(y), _comparison);

    public int GetHashCode(string path) =>
        ignoreCase ? StringComparer.OrdinalIgnoreCase.GetHashCode(Trim(path)) : Trim(path).GetHashCode(StringComparison.Ordinal);

    private static string Trim(string path) => path.Length > 1 ? path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : path;
}
