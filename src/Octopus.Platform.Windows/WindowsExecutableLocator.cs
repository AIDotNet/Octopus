namespace Octopus.Platform.Windows;

/// <summary>
/// Finds executables the way cmd does: each PATH directory, trying every PATHEXT extension unless the name already
/// has one. Not verified on real Windows.
/// </summary>
public sealed class WindowsExecutableLocator : IExecutableLocator
{
    private const string DefaultPathExt = ".COM;.EXE;.BAT;.CMD";

    private readonly string[] _extensions;

    public WindowsExecutableLocator(string? pathExt = null)
    {
        pathExt ??= Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(pathExt)) pathExt = DefaultPathExt;
        _extensions = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public IReadOnlyList<string> FindAll(string name, string? pathVariable)
    {
        if (string.IsNullOrEmpty(name)) return [];
        var names = NamesToTry(name);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> directories = name.AsSpan().IndexOfAny('\\', '/') >= 0
            ? [""] // already a path
            : (pathVariable ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var directory in directories)
        {
            foreach (var candidateName in names)
            {
                string candidate;
                try { candidate = directory.Length == 0 ? Path.GetFullPath(candidateName) : Path.Combine(directory.Trim('"'), candidateName); }
                catch (ArgumentException) { continue; }
                if (File.Exists(candidate) && seen.Add(Path.GetFullPath(candidate)))
                    result.Add(candidate);
            }
        }
        return result;
    }

    private string[] NamesToTry(string name)
    {
        var extension = Path.GetExtension(name);
        if (extension.Length > 0 && _extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return [name];
        return [.. _extensions.Select(e => name + e)];
    }
}
