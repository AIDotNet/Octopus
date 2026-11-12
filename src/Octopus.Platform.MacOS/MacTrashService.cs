using Octopus.Platform.Unix;

namespace Octopus.Platform.MacOS;

/// <summary>
/// Moves items into <c>~/.Trash</c> under a free name (<c>notes 2.txt</c>, as Finder names them). Only items on the
/// home volume: other volumes keep their own <c>.Trashes</c>, and moving across volumes would be a copy, so those
/// return false and the caller falls back to a confirmed permanent delete.
/// </summary>
/// <remarks>
/// A plain rename leaves out what <c>NSFileManager.trashItemAtURL:resultingItemURL:error:</c> adds: Finder's
/// "Put Back" (the original location is recorded by the system, not in the file) and trashing on other volumes.
/// Switching to it later only changes this class. Listing <c>~/.Trash</c> needs Full Disk Access, but renaming into it
/// and checking one name do not, which is all this does.
/// </remarks>
public sealed class MacTrashService : ITrashService
{
    private readonly string _trash;
    private readonly Func<string, string, bool> _sameVolume;

    /// <param name="trashDirectory">The trash to use; <c>~/.Trash</c> when null. Tests pass their own.</param>
    /// <param name="sameVolume">Whether an item and the trash share a volume; <see cref="UnixTrash.SameVolume"/> when null.</param>
    public MacTrashService(string? trashDirectory = null, Func<string, string, bool>? sameVolume = null)
    {
        var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _trash = trashDirectory ?? Path.Combine(home, ".Trash");
        _sameVolume = sameVolume ?? UnixTrash.SameVolume;
    }

    public bool MoveToTrash(string path)
    {
        if (OperatingSystem.IsWindows()) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!UnixTrash.EntryExists(full)) throw new FileNotFoundException("Nothing to move to the trash.", full);
        if (!Directory.Exists(_trash))
        {
            Directory.CreateDirectory(_trash, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        if (!_sameVolume(full, _trash)) return false;
        var name = Path.GetFileName(full);
        for (var number = 1; ; number++)
        {
            var target = Path.Combine(_trash, UnixTrash.NumberedName(name, number, " "));
            if (UnixTrash.EntryExists(target)) continue;
            return UnixTrash.TryRename(full, target);
        }
    }
}
