namespace Octopus.Platform;

/// <summary>
/// The system trash (Trash, Recycle Bin). Deleting through it is the default for plugins (design/plugins/04 §5): what
/// a plugin deletes by mistake can be brought back.
/// </summary>
public interface ITrashService
{
    /// <summary>
    /// Moves the file, directory or link at <paramref name="path"/> to the trash (a link goes, not its target).
    /// Returns false, leaving the item where it is, when there is no trash for it: the platform has none, or the item
    /// is on a volume the trash does not cover. The caller then asks the person and deletes permanently.
    /// </summary>
    /// <exception cref="FileNotFoundException">Nothing exists at <paramref name="path"/>.</exception>
    /// <exception cref="IOException">The trash exists but the move failed.</exception>
    /// <exception cref="UnauthorizedAccessException">The item may not be moved.</exception>
    bool MoveToTrash(string path);
}

/// <summary>No trash at all: every call says "not supported". The stand-in for tests and unknown platforms.</summary>
public sealed class NoTrashService : ITrashService
{
    public bool MoveToTrash(string path) => false;
}
