using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Octopus.Platform.Unix;

namespace Octopus.Platform.Linux;

/// <summary>
/// The home trash of the freedesktop.org Trash specification (1.0): <c>$XDG_DATA_HOME/Trash</c> with the item in
/// <c>files/</c> and a <c>info/&lt;name&gt;.trashinfo</c> beside it that records where it came from and when, so file
/// managers can list and restore it. Only items on the volume of the home trash: the spec's per-volume
/// <c>$topdir/.Trash-$uid</c> directories are not created, so other volumes return false and the caller falls back
/// to a confirmed permanent delete.
/// </summary>
public sealed class LinuxTrashService : ITrashService
{
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly string _files;
    private readonly string _info;
    private readonly Func<string, string, bool> _sameVolume;

    /// <param name="home">The user's home; <c>$HOME</c> when null.</param>
    /// <param name="xdgDataHome"><c>$XDG_DATA_HOME</c>; relative values are ignored as the base directory spec requires.</param>
    /// <param name="sameVolume">Whether an item and the trash share a volume; <see cref="UnixTrash.SameVolume"/> when null.</param>
    public LinuxTrashService(string? home = null, string? xdgDataHome = null, Func<string, string, bool>? sameVolume = null)
    {
        home ??= Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        xdgDataHome ??= Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = !string.IsNullOrEmpty(xdgDataHome) && Path.IsPathRooted(xdgDataHome) ? xdgDataHome : Path.Combine(home, ".local", "share");
        TrashDirectory = Path.Combine(dataHome, "Trash");
        _files = Path.Combine(TrashDirectory, "files");
        _info = Path.Combine(TrashDirectory, "info");
        _sameVolume = sameVolume ?? UnixTrash.SameVolume;
    }

    /// <summary>The home trash: <c>$XDG_DATA_HOME/Trash</c>.</summary>
    public string TrashDirectory { get; }

    public bool MoveToTrash(string path)
    {
        if (OperatingSystem.IsWindows()) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!UnixTrash.EntryExists(full)) throw new FileNotFoundException("Nothing to move to the trash.", full);
        // The spec: create the home trash when it is missing, private to the user.
        Directory.CreateDirectory(_files, PrivateDirectory);
        Directory.CreateDirectory(_info, PrivateDirectory);
        if (!_sameVolume(full, TrashDirectory)) return false;

        var content = InfoFile(full, DateTime.Now);
        var name = Path.GetFileName(full);
        for (var number = 1; ; number++)
        {
            var trashName = UnixTrash.NumberedName(name, number, ".");
            var target = Path.Combine(_files, trashName);
            var info = Path.Combine(_info, trashName + ".trashinfo");
            if (UnixTrash.EntryExists(target)) continue;
            // The spec reserves a name by creating its .trashinfo exclusively (O_EXCL) before the move.
            if (!TryCreateExclusive(info, content)) continue;
            try
            {
                if (UnixTrash.TryRename(full, target)) return true;
                File.Delete(info); // another volume after all: no trace left
                return false;
            }
            catch
            {
                File.Delete(info);
                throw;
            }
        }
    }

    /// <summary>The <c>.trashinfo</c> text: the original absolute path, URL-escaped, and the local deletion time.</summary>
    public static string InfoFile(string originalPath, DateTime deletedAt) =>
        "[Trash Info]\n" +
        "Path=" + EscapePath(originalPath) + "\n" +
        "DeletionDate=" + deletedAt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "\n";

    /// <summary>
    /// Escapes the path's UTF-8 bytes as in URLs (RFC 2396 §2), as the spec asks: unreserved characters and the
    /// <c>/</c> separators stay, everything else becomes <c>%XX</c>.
    /// </summary>
    public static string EscapePath(string path)
    {
        var builder = new StringBuilder(path.Length + 16);
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '!' or '~' or '*' or '\'' or '(' or ')')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return builder.ToString();
    }

    [UnsupportedOSPlatform("windows")]
    private static bool TryCreateExclusive(string path, string content)
    {
        try
        {
            using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            stream.Write(Encoding.UTF8.GetBytes(content));
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false; // taken by another trash operation
        }
    }
}
