using System.IO.Enumeration;

namespace Octopus.FileSystem;

/// <summary>What kind of row a folder browser shows.</summary>
public enum FolderEntryKind
{
    Folder,
    Drive,
    Home,
}

/// <summary>One folder (or drive root) shown in the folder browser.</summary>
public sealed record FolderBrowseEntry(
    string Name,
    string Path,
    bool IsHidden,
    bool IsExistingProject = false,
    FolderEntryKind Kind = FolderEntryKind.Folder);

/// <summary>An existing project on the same machine, offered as a quick jump in the folder browser.</summary>
public sealed record FolderBrowseProject(string Name, string Path);

/// <summary>One directory listing returned by an <see cref="IFolderBrowser"/>.</summary>
/// <param name="CurrentPath">Normalized absolute path of the shown folder; empty string stands for a virtual root (Windows drive list).</param>
/// <param name="ParentPath">Parent folder path, empty string to step up to the Windows drive list, or null when already at the top.</param>
/// <param name="Separator">The host's path separator (<c>"/"</c> or <c>"\\"</c>).</param>
/// <param name="HomePath">The host's home folder path.</param>
/// <param name="Entries">Subfolders (or drives at the virtual root), sorted naturally.</param>
/// <param name="Roots">Root entries for the quick-access rail (drives on Windows, <c>"/"</c> on Unix).</param>
/// <param name="ExistingProjects">Projects already known on the host, when available.</param>
public sealed record FolderBrowsePage(
    string CurrentPath,
    string? ParentPath,
    string Separator,
    string HomePath,
    IReadOnlyList<FolderBrowseEntry> Entries,
    IReadOnlyList<FolderBrowseEntry> Roots,
    IReadOnlyList<FolderBrowseProject>? ExistingProjects = null);

/// <summary>
/// Browses folders on a machine (this computer, a paired Octopus host, or an SSH host) for picking a project folder.
/// Failures throw <see cref="IOException"/> with a message suitable for showing to the user.
/// </summary>
public interface IFolderBrowser
{
    /// <summary>Human-readable name of the machine being browsed (for the dialog title).</summary>
    string HostDisplayName { get; }

    /// <summary>True when creating a new folder inside a browsed directory is supported.</summary>
    bool CanCreateFolder { get; }

    /// <summary>
    /// Lists the subfolders of <paramref name="path"/>. Null or empty starts at the machine's home folder unless
    /// <paramref name="listRoots"/> is true (which shows the drive list on Windows or <c>/</c> on Unix).
    /// When <paramref name="fallbackToParent"/> is true and <paramref name="path"/> does not exist, walks up to the
    /// nearest existing ancestor (or home).
    /// </summary>
    Task<FolderBrowsePage> BrowseAsync(string? path = null, bool listRoots = false, bool fallbackToParent = false, CancellationToken cancellationToken = default);

    /// <summary>Creates a new subfolder named <paramref name="name"/> inside <paramref name="parentPath"/> and returns its full path.</summary>
    Task<string> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken = default);
}

/// <summary>Cross-OS path helpers for the folder browser UI and implementations.</summary>
public static class FolderBrowserPaths
{
    /// <summary>One clickable segment of a breadcrumb bar.</summary>
    public sealed record BreadcrumbSegment(string Label, string FullPath);

    /// <summary>
    /// Checks a new folder name before sending it to any machine (local, Windows, Unix, SSH).
    /// Returns the trimmed name or throws <see cref="ArgumentException"/> when invalid.
    /// </summary>
    public static string ValidateFolderName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0) throw new ArgumentException("Folder name cannot be empty.", nameof(name));
        if (trimmed is "." or "..") throw new ArgumentException("Folder name cannot be '.' or '..'.", nameof(name));
        if (trimmed.IndexOfAny(['/', '\\', '\0']) >= 0) throw new ArgumentException("Folder name cannot contain path separators.", nameof(name));
        return trimmed;
    }

    /// <summary>Splits an absolute path (Windows, UNC, or Unix) into clickable breadcrumb segments.</summary>
    public static IReadOnlyList<BreadcrumbSegment> SplitBreadcrumb(string path, string separator)
    {
        if (string.IsNullOrEmpty(path)) return [];

        // Windows drive path: C:\ or C:\Users\token
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            var sep = separator.Length > 0 ? separator : "\\";
            var drive = path[..2];
            var driveRoot = drive + sep;
            var segments = new List<BreadcrumbSegment> { new(drive, driveRoot) };
            var rest = path.Length > 2 ? path[2..].TrimStart('\\', '/') : "";
            var current = driveRoot;
            foreach (var part in rest.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            {
                current = current.EndsWith(sep, StringComparison.Ordinal) ? current + part : current + sep + part;
                segments.Add(new BreadcrumbSegment(part, current));
            }
            return segments;
        }

        // UNC path: \\server\share\dir
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return [new(path, path)];
            if (parts.Length == 1) return [new(@"\\" + parts[0], @"\\" + parts[0])];
            var root = @"\\" + parts[0] + @"\" + parts[1];
            var segments = new List<BreadcrumbSegment> { new(root, root) };
            var current = root;
            for (var i = 2; i < parts.Length; i++)
            {
                current = current + @"\" + parts[i];
                segments.Add(new BreadcrumbSegment(parts[i], current));
            }
            return segments;
        }

        // POSIX path: / or /Users/token/code
        if (path.StartsWith('/'))
        {
            var segments = new List<BreadcrumbSegment> { new("/", "/") };
            var current = "";
            foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = current + "/" + part;
                segments.Add(new BreadcrumbSegment(part, current));
            }
            return segments;
        }

        return [new(path, path)];
    }

    /// <summary>Expands a leading <c>~</c>, <c>~/</c> or <c>~\</c> against <paramref name="home"/>.</summary>
    public static string ExpandHome(string path, string home) =>
        path == "~" ? home
        : path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(home, path[2..])
            : path;
}

/// <summary>
/// Browses folders on this computer (folders only, naturally sorted, with Windows drive roots and optional existing
/// project markers). Also reused on the host side of Octopus remote browsing so local and remote behave identically.
/// </summary>
public sealed class LocalFolderBrowser : IFolderBrowser
{
    private static readonly EnumerationOptions ListOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
    };

    private readonly Func<IReadOnlyList<FolderBrowseProject>>? _existingProjects;
    private readonly IEqualityComparer<string> _pathComparer;

    public LocalFolderBrowser(
        string hostDisplayName,
        Func<IReadOnlyList<FolderBrowseProject>>? existingProjects = null,
        IEqualityComparer<string>? pathComparer = null)
    {
        HostDisplayName = hostDisplayName;
        _existingProjects = existingProjects;
        _pathComparer = pathComparer ?? (OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
    }

    public string HostDisplayName { get; }
    public bool CanCreateFolder => true;

    public Task<FolderBrowsePage> BrowseAsync(string? path = null, bool listRoots = false, bool fallbackToParent = false, CancellationToken cancellationToken = default) =>
        Task.Run(() => BrowseCore(path, listRoots, fallbackToParent, cancellationToken), cancellationToken);

    public Task<string> CreateFolderAsync(string parentPath, string name, CancellationToken cancellationToken = default) =>
        Task.Run(() => CreateFolderCore(parentPath, name), cancellationToken);

    private FolderBrowsePage BrowseCore(string? path, bool listRoots, bool fallbackToParent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var separator = Path.DirectorySeparatorChar.ToString();
        var roots = EnumerateRoots();
        var projects = _existingProjects?.Invoke();
        var projectSet = projects is null
            ? null
            : new HashSet<string>(projects.Select(p => NormalizeForCompare(p.Path)), _pathComparer);

        // Windows virtual root ("This PC" drive list).
        if (listRoots && OperatingSystem.IsWindows())
        {
            return new FolderBrowsePage(
                CurrentPath: "",
                ParentPath: null,
                Separator: separator,
                HomePath: home,
                Entries: roots,
                Roots: roots,
                ExistingProjects: projects);
        }

        string target;
        if (listRoots)
        {
            target = "/";
        }
        else if (string.IsNullOrWhiteSpace(path))
        {
            target = home;
        }
        else
        {
            var expanded = FolderBrowserPaths.ExpandHome(path.Trim(), home);
            try
            {
                target = Path.GetFullPath(expanded);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                if (!fallbackToParent) throw new IOException(e.Message, e);
                target = home;
            }
        }

        if (!Directory.Exists(target))
        {
            if (!fallbackToParent)
            {
                throw new DirectoryNotFoundException($"The folder '{target}' does not exist.");
            }
            target = FindExistingAncestor(target, home);
        }

        target = NormalizeDisplayPath(target);
        var entries = EnumerateFolders(target, projectSet, cancellationToken);
        var parent = ParentOf(target);

        return new FolderBrowsePage(
            CurrentPath: target,
            ParentPath: parent,
            Separator: separator,
            HomePath: home,
            Entries: entries,
            Roots: roots,
            ExistingProjects: projects);
    }

    private string CreateFolderCore(string parentPath, string name)
    {
        var cleanName = FolderBrowserPaths.ValidateFolderName(name);
        if (cleanName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Folder name contains invalid characters.", nameof(name));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var trimmedParent = (parentPath ?? "").Trim();
        if (trimmedParent.Length == 0)
        {
            throw new IOException("Cannot create a folder here.");
        }

        string parentFull;
        try
        {
            parentFull = Path.GetFullPath(FolderBrowserPaths.ExpandHome(trimmedParent, home));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new IOException(e.Message, e);
        }

        if (!Directory.Exists(parentFull))
        {
            throw new DirectoryNotFoundException($"The folder '{parentFull}' does not exist.");
        }

        var target = Path.Combine(parentFull, cleanName);
        if (File.Exists(target) || Directory.Exists(target))
        {
            throw new IOException($"A folder or file named '{cleanName}' already exists.");
        }

        try
        {
            Directory.CreateDirectory(target);
        }
        catch (UnauthorizedAccessException e)
        {
            throw new IOException(e.Message, e);
        }
        return NormalizeDisplayPath(target);
    }

    private static IReadOnlyList<FolderBrowseEntry> EnumerateFolders(string directory, HashSet<string>? projectSet, CancellationToken cancellationToken)
    {
        try
        {
            var entries = new List<FolderBrowseEntry>();
            var enumerable = new FileSystemEnumerable<FolderBrowseEntry?>(directory, (ref FileSystemEntry e) =>
            {
                if (!e.IsDirectory) return null;
                var name = e.FileName.ToString();
                var full = NormalizeDisplayPath(e.ToFullPath());
                var isHidden = name.StartsWith('.') || e.IsHidden;
                var isProject = projectSet is not null && projectSet.Contains(NormalizeForCompare(full));
                return new FolderBrowseEntry(name, full, isHidden, isProject, FolderEntryKind.Folder);
            }, ListOptions);

            foreach (var entry in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry is not null) entries.Add(entry);
            }

            entries.Sort(static (a, b) => NaturalStringComparer.Instance.Compare(a.Name, b.Name));
            return entries;
        }
        catch (UnauthorizedAccessException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    private static IReadOnlyList<FolderBrowseEntry> EnumerateRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [new FolderBrowseEntry("/", "/", IsHidden: false, Kind: FolderEntryKind.Drive)];
        }

        var list = new List<FolderBrowseEntry>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    var root = drive.RootDirectory.FullName;
                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? root.TrimEnd('\\')
                        : $"{root.TrimEnd('\\')} ({drive.VolumeLabel})";
                    list.Add(new FolderBrowseEntry(label, root, IsHidden: false, Kind: FolderEntryKind.Drive));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return list;
    }

    private static string FindExistingAncestor(string path, string fallback)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            if (Directory.Exists(current)) return current;
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal)) break;
            current = parent;
        }
        return fallback;
    }

    private static string? ParentOf(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var trimmed = Path.TrimEndingDirectorySeparator(fullPath);
            // Drive root like "C:\" -> parent is "" (virtual drive list).
            var parent = Path.GetDirectoryName(trimmed);
            return parent is null ? "" : NormalizeDisplayPath(parent);
        }
        if (fullPath == "/") return null;
        var unixParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(fullPath));
        return string.IsNullOrEmpty(unixParent) ? "/" : NormalizeDisplayPath(unixParent);
    }

    private static string NormalizeDisplayPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root) && string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }
        return Path.TrimEndingDirectorySeparator(path);
    }

    private static string NormalizeForCompare(string path) =>
        Path.TrimEndingDirectorySeparator(path);
}
