namespace Octopus.Platform.Unix;

/// <summary>
/// Lists every executable of a given name along PATH. All matches are returned because the first one may be a
/// stale shim; duplicates are folded by real path so that a symlinked directory on PATH does not list a file twice.
/// </summary>
public sealed class UnixExecutableLocator : IExecutableLocator
{
    public IReadOnlyList<string> FindAll(string name, string? pathVariable)
    {
        if (string.IsNullOrEmpty(name)) return [];
        if (name.Contains('/'))
            return UnixFile.IsExecutableFile(name) ? [Path.GetFullPath(name)] : [];

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in (pathVariable ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.Combine(directory, name); }
            catch (ArgumentException) { continue; }
            if (!UnixFile.IsExecutableFile(candidate)) continue;
            if (seen.Add(UnixFile.ResolveRealPath(candidate)))
                result.Add(candidate);
        }
        return result;
    }
}
